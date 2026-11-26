using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// The coarse threat lane at evaluation time.
//
// Enumerates the SAME filtered relation multiset the training data carries -
// the DataGen coarse-encode logic ported from record decoding to live Board
// bitboards (pawns threaten P/N/R; minors and rooks P/N/B/R; knights and
// queens P/N/B/R/Q; kings neither attack nor are attacked; the pawn stopped
// dead by a pawn; NO symmetric deduplication, multiplicity is the signal) -
// and adds each relation's weight row on top of the two perspective
// accumulators. The incremental accumulator itself never learns about the
// lane: coarse content is paid per EVALUATION, never per node, which is the
// entire reason this lane can afford what the fine set could not. The C#
// encoder this mirrors has 3000/3000 parity with the trainer's own
// enumeration, and verify_export closes the loop on the exported file.
//
// HOW THE COST IS PAID (5.5.0 redesign, node-identical to the first cut).
// The profiler split the first cut's ~1,500 ns per evaluation into ~150 ns
// of classification and ~900 ns of ROW ARITHMETIC: a busy middlegame holds
// ~50 relations in ~40 distinct buckets, and streaming two 256-byte rows per
// bucket is near the hardware floor already. The row count is what had to
// fall, and it falls because consecutive evaluations along a search path
// share almost all of their histogram - a move changes a handful of the 144
// buckets. So the lane is kept as STATE: the last histogram and the lane's
// own two sums (white-relative and black-relative, so the side to move never
// forces a recomputation), and each evaluation classifies the position
// (cheap), diffs the histogram against the previous one, and streams rows
// only for the buckets whose count changed, scaled by the signed delta.
// Integer arithmetic makes this exact: sum_new = sum_old + sum(delta * row),
// order-independent and wrap-consistent in int16, so every accumulator value
// is bit-identical to summing the whole histogram from zero.
public sealed class NnueCoarseLane
{
    private const int Buckets = 144;
    // Per-level histogram stride: 144 rounded up to whole cache lines, so
    // every level's histogram starts on a 64-byte boundary.
    private const int HistStride = 192;

    // ONE STATE PER SEARCH LEVEL (6.5.0). The lane used to keep a single
    // state - the last evaluated position - and diff every evaluation against
    // it. In a search the last evaluated position is usually a cousin, not
    // the parent: after a sibling's subtree the diff has to undo that subtree
    // and then apply this move, roughly twice the rows of one move. Now every
    // level of the accumulator stack has its own histogram and lane sums, and
    // an evaluation diffs against whichever of two bases differs in fewer
    // live buckets: the nearest ancestor evaluated on the current path, or
    // the last evaluated level.
    //
    // THE CHOICE OF BASE CAN NEVER CHANGE A VALUE. Every level's histogram
    // and lane sums are written together and are always a consistent pair
    // (sum of row x count over that histogram, in wrapping int16), starting
    // from the all-zero pair; the new position is classified from the board
    // every time. So any level is a valid base and the result is bit-identical
    // whichever is picked - the push/pop bookkeeping only decides how many
    // rows are streamed, never what comes out.
    private readonly int _levels;
    private readonly byte[] _counts = NnueAlignedArrays.Allocate<byte>(HistStride);
    private readonly byte[] _hist;
    private readonly short[] _laneWhite;
    private readonly short[] _laneBlack;
    private readonly bool[] _computed;
    private int _last;
    private readonly int _ftOut;
    private NnueNetwork? _liveFor;
    private ulong _live0, _live1, _live2;

    public NnueCoarseLane(int ftOut, int levels = 1)
    {
        _ftOut = ftOut;
        _levels = Math.Max(1, levels);
        _hist = NnueAlignedArrays.Allocate<byte>(_levels * HistStride);
        _laneWhite = NnueAlignedArrays.Allocate<short>(_levels * ftOut);
        _laneBlack = NnueAlignedArrays.Allocate<short>(_levels * ftOut);
        _computed = new bool[_levels];
    }

    // The stack pushed a new position at 'level': its state is a sibling's.
    public void OnPush(int level)
    {
        if ((uint)level < (uint)_levels)
            _computed[level] = false;
    }

    // A new root at level 0.
    public void OnReset() => _computed[0] = false;

    // outStm/outOpp receive accumulator + lane; they must be FtOutputs long.
    public void Apply(NnueNetwork net, Board board,
                      short[] stmAcc, short[] oppAcc,
                      short[] outStm, short[] outOpp)
        => Apply(net, board, 0, stmAcc, oppAcc, outStm, outOpp);

    [SkipLocalsInit]
    public void Apply(NnueNetwork net, Board board, int level,
                      short[] stmAcc, short[] oppAcc,
                      short[] outStm, short[] outOpp)
    {
        int ftOut = _ftOut;
        if ((uint)level >= (uint)_levels)
            level = _levels - 1;
        NnueCoarse.Classify(board, _counts);

        short[] weights = net.CoarseWeights!;
        if (!ReferenceEquals(_liveFor, net))
            SetLiveMask(net);

        // The base: the nearest ancestor evaluated on the current path, or
        // the last evaluated level, whichever differs in fewer live buckets.
        int anc = level;
        while (anc >= 0 && !_computed[anc])
            anc--;
        int baseLevel = _last;
        if (anc >= 0 && anc != _last && LiveChanged(anc) <= LiveChanged(_last))
            baseLevel = anc;
        if (NnueProfiling.Enabled)
        {
            NnueProfiling.CoarseApplies++;
            NnueProfiling.CoarseRows += LiveChanged(baseLevel);
            NnueProfiling.CoarseRowsVsLast += LiveChanged(_last);
        }

        // The histogram diff, vectorised (audit of 2026-10-04): 144 = 4 x 32
        // + 16 bytes compared a block at a time, the signed deltas stored in
        // one go, a mask of the changed buckets kept, and the new histogram
        // written as this level's. A byte delta is exact as a signed byte
        // because a count never exceeds 127: a bucket counts relations between
        // one attacker class and one victim class, at most ten pieces each
        // (two plus eight promotions), plus at most eight pawns stopped by a
        // pawn.
        ref byte cur = ref MemoryMarshal.GetArrayDataReference(_counts);
        ref byte prev = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_hist), baseLevel * HistStride);
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_hist), level * HistStride);
        Span<sbyte> deltas = stackalloc sbyte[Buckets];
        ref sbyte dRef = ref MemoryMarshal.GetReference(deltas);
        ulong m0 = 0, m1 = 0, m2;
        for (int block = 0; block < 4; block++)
        {
            int baseIdx = block * 32;
            var c = Vector256.LoadUnsafe(ref cur, (nuint)baseIdx);
            var pv = Vector256.LoadUnsafe(ref prev, (nuint)baseIdx);
            ulong changed = ~Vector256.ExtractMostSignificantBits(Vector256.Equals(c, pv)) & 0xFFFFFFFFUL;
            (c - pv).AsSByte().StoreUnsafe(ref dRef, (nuint)baseIdx);
            c.StoreUnsafe(ref dst, (nuint)baseIdx);
            if (block < 2)
                m0 |= changed << (32 * block);
            else
                m1 |= changed << (32 * (block - 2));
        }
        {
            var c = Vector128.LoadUnsafe(ref cur, 128);
            var pv = Vector128.LoadUnsafe(ref prev, 128);
            m2 = ~Vector128.ExtractMostSignificantBits(Vector128.Equals(c, pv)) & 0xFFFFUL;
            (c - pv).AsSByte().StoreUnsafe(ref dRef, 128);
            c.StoreUnsafe(ref dst, 128);
        }
        // Buckets whose row and mirror row are both all zero add nothing over
        // 2 x ftOut lanes: 62 of the 144 in the shipping net, so masking them
        // out here is the common case, not a corner.
        m0 &= _live0;
        m1 &= _live1;
        m2 &= _live2;

        // The live changed buckets, as (white row, black row) offset pairs
        // grouped by their delta (+1, -1, anything else), so the row pass
        // below runs without a branch per row.
        Span<int> plus = stackalloc int[2 * Buckets];
        Span<int> minus = stackalloc int[2 * Buckets];
        Span<int> other = stackalloc int[2 * Buckets];
        Span<short> otherK = stackalloc short[Buckets];
        int nPlus = 0, nMinus = 0, nOther = 0;
        for (int w = 0; w < 3; w++)
        {
            ulong mask = w == 0 ? m0 : w == 1 ? m1 : m2;
            while (mask != 0)
            {
                int pair = w * 64 + System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int delta = deltas[pair];
                int attCode = pair / 12, vicCode = pair % 12;
                int whiteRow = pair * ftOut;
                int blackRow = (((attCode + 6) % 12) * 12 + (vicCode + 6) % 12) * ftOut;
                if (delta == 1)
                {
                    plus[2 * nPlus] = whiteRow;
                    plus[2 * nPlus + 1] = blackRow;
                    nPlus++;
                }
                else if (delta == -1)
                {
                    minus[2 * nMinus] = whiteRow;
                    minus[2 * nMinus + 1] = blackRow;
                    nMinus++;
                }
                else
                {
                    other[2 * nOther] = whiteRow;
                    other[2 * nOther + 1] = blackRow;
                    otherK[nOther] = (short)delta;
                    nOther++;
                }
            }
        }

        // The white lane pairs with the accumulator of the side whose
        // perspective is white-relative: the side to move when White is to
        // move, the opponent otherwise.
        bool stmIsBlack = board.SideToMove == Color.Black;
        NnueCoarse.AddRowsAndSum(
            _laneWhite.AsSpan(baseLevel * ftOut, ftOut), _laneBlack.AsSpan(baseLevel * ftOut, ftOut),
            _laneWhite.AsSpan(level * ftOut, ftOut), _laneBlack.AsSpan(level * ftOut, ftOut),
            weights, net.CoarseWeightsI8,
            plus[..(2 * nPlus)], minus[..(2 * nMinus)], other[..(2 * nOther)], otherK[..nOther],
            stmIsBlack ? oppAcc : stmAcc, stmIsBlack ? stmAcc : oppAcc,
            stmIsBlack ? outOpp : outStm, stmIsBlack ? outStm : outOpp, ftOut);
        _computed[level] = true;
        _last = level;
    }

    // Live buckets of _counts that differ from the histogram stored at 'level'.
    private int LiveChanged(int level)
    {
        ref byte cur = ref MemoryMarshal.GetArrayDataReference(_counts);
        ref byte prev = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_hist), level * HistStride);
        ulong m0 = 0, m1 = 0;
        for (int block = 0; block < 4; block++)
        {
            ulong eq = Vector256.ExtractMostSignificantBits(Vector256.Equals(
                Vector256.LoadUnsafe(ref cur, (nuint)(block * 32)), Vector256.LoadUnsafe(ref prev, (nuint)(block * 32))));
            ulong changed = ~eq & 0xFFFFFFFFUL;
            if (block < 2)
                m0 |= changed << (32 * block);
            else
                m1 |= changed << (32 * (block - 2));
        }
        ulong m2 = ~Vector128.ExtractMostSignificantBits(Vector128.Equals(
            Vector128.LoadUnsafe(ref cur, 128), Vector128.LoadUnsafe(ref prev, 128))) & 0xFFFFUL;
        return System.Numerics.BitOperations.PopCount(m0 & _live0)
             + System.Numerics.BitOperations.PopCount(m1 & _live1)
             + System.Numerics.BitOperations.PopCount(m2 & _live2);
    }

    // Also (re)starts every level from the all-zero pair: lane sums built
    // from another net's rows would not be consistent with this one's.
    private void SetLiveMask(NnueNetwork net)
    {
        Array.Clear(_hist);
        Array.Clear(_laneWhite);
        Array.Clear(_laneBlack);
        Array.Clear(_computed);
        _last = 0;
        bool[]? dead = net.CoarseRowDead;
        _live0 = _live1 = _live2 = 0;
        for (int pair = 0; pair < Buckets; pair++)
        {
            if (dead is not null && dead[pair])
                continue;
            if (pair < 64) _live0 |= 1UL << pair;
            else if (pair < 128) _live1 |= 1UL << (pair - 64);
            else _live2 |= 1UL << (pair - 128);
        }
        _liveFor = net;
    }
}

public static class NnueCoarse
{
    private const ulong NotFileA = 0xFEFEFEFEFEFEFEFE;
    private const ulong NotFileH = 0x7F7F7F7F7F7F7F7F;

    // Fills the 144-bucket relation histogram for the position and returns
    // the number of relations. Public so the profiler can price the
    // classification half of the lane apart from the row arithmetic.
    public static int Classify(Board board, Span<byte> counts)
    {
        counts.Clear();
        ulong occupancy = board.AllOccupancy;
        ulong whitePawns = board.Pieces(Color.White, PieceType.Pawn);
        ulong blackPawns = board.Pieces(Color.Black, PieceType.Pawn);
        ulong pawns = whitePawns | blackPawns;
        ulong knights = board.Pieces(Color.White, PieceType.Knight)
                      | board.Pieces(Color.Black, PieceType.Knight);
        ulong bishops = board.Pieces(Color.White, PieceType.Bishop)
                      | board.Pieces(Color.Black, PieceType.Bishop);
        ulong rooks = board.Pieces(Color.White, PieceType.Rook)
                    | board.Pieces(Color.Black, PieceType.Rook);
        ulong queens = board.Pieces(Color.White, PieceType.Queen)
                     | board.Pieces(Color.Black, PieceType.Queen);
        ulong pawnTargets = pawns | knights | rooks;
        ulong minorSliderTargets = pawnTargets | bishops;
        ulong queenTargets = minorSliderTargets | queens;
        int relations = 0;

        for (int c = 0; c < 2; c++)
        {
            int attacker = c * 6;
            ulong cPawns = c == 0 ? whitePawns : blackPawns;

            ulong capA = c == 0 ? (cPawns & NotFileH) << 9 : (cPawns & NotFileH) >> 7;
            ulong capB = c == 0 ? (cPawns & NotFileA) << 7 : (cPawns & NotFileA) >> 9;
            relations += CountHits(counts, board, attacker * 12, capA & pawnTargets);
            relations += CountHits(counts, board, attacker * 12, capB & pawnTargets);

            // The pawn stopped dead by a pawn of either colour: the victim is
            // the blocker, and only pawns can be blockers here.
            ulong pushers = (c == 0 ? pawns >> 8 : pawns << 8) & cPawns;
            ulong blocked = c == 0 ? pushers << 8 : pushers >> 8;
            relations += CountHits(counts, board, attacker * 12, blocked);

            for (int pt = 1; pt <= 4; pt++)
            {
                attacker = c * 6 + pt;
                ulong targets = (pt == 1 || pt == 4) ? queenTargets : minorSliderTargets;
                ulong from = board.Pieces((Color)c, (PieceType)pt);
                while (from != 0)
                {
                    int sq = System.Numerics.BitOperations.TrailingZeroCount(from);
                    from &= from - 1;
                    ulong att = pt switch
                    {
                        1 => Attacks.Knight(sq),
                        2 => Attacks.Bishop(sq, occupancy),
                        3 => Attacks.Rook(sq, occupancy),
                        _ => Attacks.Queen(sq, occupancy),
                    };
                    relations += CountHits(counts, board, attacker * 12, att & targets);
                }
            }
        }
        return relations;
    }

    // One attacker's hit set: every set bit is one relation, classified by a
    // single mailbox read.
    private static int CountHits(Span<byte> counts, Board board, int row, ulong hits)
    {
        int n = System.Numerics.BitOperations.PopCount(hits);
        // Every hit square holds a piece, so the white ones are hits & White occupancy and the rest are black:
        // the colour offset of the bucket is then a constant per half and ColorAt is never asked.
        ulong white = hits & board.Occupancy(Color.White);
        ulong black = hits ^ white;
        while (white != 0)
        {
            int to = System.Numerics.BitOperations.TrailingZeroCount(white);
            white &= white - 1;
            counts[row + (int)board.PieceTypeAt(to)]++;
        }
        row += 6;
        while (black != 0)
        {
            int to = System.Numerics.BitOperations.TrailingZeroCount(black);
            black &= black - 1;
            counts[row + (int)board.PieceTypeAt(to)]++;
        }
        return n;
    }

    // a += n * weights[rowA ..), b += n * weights[rowB ..), n signed, in one
    // pass so the two independent streams overlap. int16 wrap-around on
    // purpose: identical to adding (or removing) each row |n| times.
    internal static void AddRows(short[] a, short[] b, short[] weights,
                                 int rowA, int rowB, int n, int ftOut)
    {
        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short ra = ref MemoryMarshal.GetArrayDataReference(a);
            ref short rb = ref MemoryMarshal.GetArrayDataReference(b);
            ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
            nuint oa = (nuint)rowA, ob = (nuint)rowB;
            if (n == 1)
            {
                for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                {
                    (Vector256.LoadUnsafe(ref ra, i) + Vector256.LoadUnsafe(ref w, oa + i)).StoreUnsafe(ref ra, i);
                    (Vector256.LoadUnsafe(ref rb, i) + Vector256.LoadUnsafe(ref w, ob + i)).StoreUnsafe(ref rb, i);
                }
            }
            else if (n == -1)
            {
                for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                {
                    (Vector256.LoadUnsafe(ref ra, i) - Vector256.LoadUnsafe(ref w, oa + i)).StoreUnsafe(ref ra, i);
                    (Vector256.LoadUnsafe(ref rb, i) - Vector256.LoadUnsafe(ref w, ob + i)).StoreUnsafe(ref rb, i);
                }
            }
            else
            {
                Vector256<short> k = Vector256.Create((short)n);
                for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                {
                    (Vector256.LoadUnsafe(ref ra, i) + Vector256.LoadUnsafe(ref w, oa + i) * k).StoreUnsafe(ref ra, i);
                    (Vector256.LoadUnsafe(ref rb, i) + Vector256.LoadUnsafe(ref w, ob + i) * k).StoreUnsafe(ref rb, i);
                }
            }
            return;
        }

        for (int i = 0; i < ftOut; i++)
        {
            a[i] = (short)(a[i] + n * weights[rowA + i]);
            b[i] = (short)(b[i] + n * weights[rowB + i]);
        }
    }

    // The whole lane update and both sums in ONE pass over the lane, a
    // register block at a time: each 16-lane block of the two base lane sums
    // is loaded once, every changed row (pairs of white/black row offsets,
    // grouped by delta +1, -1 and other) is folded into it in registers, and
    // it is stored once into the destination lane, together with accumulator
    // + lane for both perspectives. The per-row version reloaded and restored
    // the two lane sums for every changed bucket and then read them again to
    // sum. Source and destination may be the same lane. int16 wrap-around
    // makes the result bit-identical whatever the row order.
    internal static void AddRowsAndSum(ReadOnlySpan<short> srcW, ReadOnlySpan<short> srcB,
                                       Span<short> dstW, Span<short> dstB, short[] weights, sbyte[]? weightsI8,
                                       ReadOnlySpan<int> plus, ReadOnlySpan<int> minus,
                                       ReadOnlySpan<int> other, ReadOnlySpan<short> otherK,
                                       short[] accW, short[] accB, short[] outW, short[] outB, int ftOut)
    {
        if (!Avx2.IsSupported || ftOut % Vector256<short>.Count != 0)
        {
            for (int i = 0; i < ftOut; i++)
            {
                int vwS = srcW[i], vbS = srcB[i];
                for (int j = 0; j < plus.Length; j += 2)
                {
                    vwS += weights[plus[j] + i];
                    vbS += weights[plus[j + 1] + i];
                }
                for (int j = 0; j < minus.Length; j += 2)
                {
                    vwS -= weights[minus[j] + i];
                    vbS -= weights[minus[j + 1] + i];
                }
                for (int j = 0; j < other.Length; j += 2)
                {
                    vwS += otherK[j >> 1] * weights[other[j] + i];
                    vbS += otherK[j >> 1] * weights[other[j + 1] + i];
                }
                dstW[i] = (short)vwS;
                dstB[i] = (short)vbS;
                outW[i] = (short)(accW[i] + (short)vwS);
                outB[i] = (short)(accB[i] + (short)vbS);
            }
            return;
        }

        ref short sw = ref MemoryMarshal.GetReference(srcW);
        ref short sb = ref MemoryMarshal.GetReference(srcB);
        ref short lw = ref MemoryMarshal.GetReference(dstW);
        ref short lb = ref MemoryMarshal.GetReference(dstB);
        ref short aw = ref MemoryMarshal.GetArrayDataReference(accW);
        ref short ab = ref MemoryMarshal.GetArrayDataReference(accB);
        ref short ow = ref MemoryMarshal.GetArrayDataReference(outW);
        ref short ob = ref MemoryMarshal.GetArrayDataReference(outB);
        ref int rp = ref MemoryMarshal.GetReference(plus);
        ref int rm = ref MemoryMarshal.GetReference(minus);
        ref int ro = ref MemoryMarshal.GetReference(other);
        ref short rk = ref MemoryMarshal.GetReference(otherK);
        int np = plus.Length, nm = minus.Length, no = other.Length;
        if (weightsI8 is not null)
        {
            // The int8 copy of the same rows, sign-extended on load
            // (VPMOVSXBW): identical int16 values, half the bytes fetched.
            ref sbyte w8 = ref MemoryMarshal.GetArrayDataReference(weightsI8);
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
            {
                Vector256<short> vw = Vector256.LoadUnsafe(ref sw, i);
                Vector256<short> vb = Vector256.LoadUnsafe(ref sb, i);
                for (int j = 0; j < np; j += 2)
                {
                    vw += Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref rp, j) + i));
                    vb += Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref rp, j + 1) + i));
                }
                for (int j = 0; j < nm; j += 2)
                {
                    vw -= Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref rm, j) + i));
                    vb -= Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref rm, j + 1) + i));
                }
                for (int j = 0; j < no; j += 2)
                {
                    Vector256<short> k = Vector256.Create(Unsafe.Add(ref rk, j >> 1));
                    vw += Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref ro, j) + i)) * k;
                    vb += Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref w8, (nuint)Unsafe.Add(ref ro, j + 1) + i)) * k;
                }
                vw.StoreUnsafe(ref lw, i);
                vb.StoreUnsafe(ref lb, i);
                (Vector256.LoadUnsafe(ref aw, i) + vw).StoreUnsafe(ref ow, i);
                (Vector256.LoadUnsafe(ref ab, i) + vb).StoreUnsafe(ref ob, i);
            }
            return;
        }

        ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
        for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
        {
            Vector256<short> vw = Vector256.LoadUnsafe(ref sw, i);
            Vector256<short> vb = Vector256.LoadUnsafe(ref sb, i);
            for (int j = 0; j < np; j += 2)
            {
                vw += Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref rp, j) + i);
                vb += Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref rp, j + 1) + i);
            }
            for (int j = 0; j < nm; j += 2)
            {
                vw -= Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref rm, j) + i);
                vb -= Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref rm, j + 1) + i);
            }
            for (int j = 0; j < no; j += 2)
            {
                Vector256<short> k = Vector256.Create(Unsafe.Add(ref rk, j >> 1));
                vw += Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref ro, j) + i) * k;
                vb += Vector256.LoadUnsafe(ref w, (nuint)Unsafe.Add(ref ro, j + 1) + i) * k;
            }
            vw.StoreUnsafe(ref lw, i);
            vb.StoreUnsafe(ref lb, i);
            (Vector256.LoadUnsafe(ref aw, i) + vw).StoreUnsafe(ref ow, i);
            (Vector256.LoadUnsafe(ref ab, i) + vb).StoreUnsafe(ref ob, i);
        }
    }

    // output = accumulator + lane, one pass.
    internal static void Sum(short[] acc, short[] lane, short[] output, int ftOut)
    {
        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short x = ref MemoryMarshal.GetArrayDataReference(acc);
            ref short l = ref MemoryMarshal.GetArrayDataReference(lane);
            ref short o = ref MemoryMarshal.GetArrayDataReference(output);
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                (Vector256.LoadUnsafe(ref x, i) + Vector256.LoadUnsafe(ref l, i)).StoreUnsafe(ref o, i);
            return;
        }
        for (int i = 0; i < ftOut; i++)
            output[i] = (short)(acc[i] + lane[i]);
    }
}
