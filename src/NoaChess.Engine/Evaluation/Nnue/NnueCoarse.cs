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

    private readonly byte[] _counts = new byte[Buckets];
    private readonly byte[] _previous = new byte[Buckets];
    private readonly short[] _laneWhite;
    private readonly short[] _laneBlack;
    private readonly int _ftOut;

    public NnueCoarseLane(int ftOut)
    {
        _ftOut = ftOut;
        _laneWhite = new short[ftOut];
        _laneBlack = new short[ftOut];
    }

    // outStm/outOpp receive accumulator + lane; they must be FtOutputs long.
    public void Apply(NnueNetwork net, Board board,
                      short[] stmAcc, short[] oppAcc,
                      short[] outStm, short[] outOpp)
    {
        int ftOut = _ftOut;
        NnueCoarse.Classify(board, _counts);

        short[] weights = net.CoarseWeights!;
        for (int pair = 0; pair < Buckets; pair++)
        {
            int delta = _counts[pair] - _previous[pair];
            if (delta == 0)
                continue;
            _previous[pair] = _counts[pair];
            int attCode = pair / 12, vicCode = pair % 12;
            int whiteRow = pair * ftOut;
            int blackRow = (((attCode + 6) % 12) * 12 + (vicCode + 6) % 12) * ftOut;
            NnueCoarse.AddRows(_laneWhite, _laneBlack, weights, whiteRow, blackRow, delta, ftOut);
        }

        bool stmIsBlack = board.SideToMove == Color.Black;
        NnueCoarse.Sum(stmAcc, stmIsBlack ? _laneBlack : _laneWhite, outStm, ftOut);
        NnueCoarse.Sum(oppAcc, stmIsBlack ? _laneWhite : _laneBlack, outOpp, ftOut);
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
        while (hits != 0)
        {
            int to = System.Numerics.BitOperations.TrailingZeroCount(hits);
            hits &= hits - 1;
            counts[row + (int)board.ColorAt(to) * 6 + (int)board.PieceTypeAt(to)]++;
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
