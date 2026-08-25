using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// "Finny table": a per-perspective, per-king-square cache of feature-transformer
// accumulators, so a king move costs a DIFF instead of a full recomputation.
//
// WHY. Every feature in HalfKAv2_hm is king-relative, so when a perspective's
// king moves that whole perspective is invalidated and has to be rebuilt. The
// old path rebuilt it from the bias by adding all ~32 active feature rows. At
// FT=128 that is 32 rows x 128 int16; at the widths v4.2.0 targets (512, 1024)
// the same refresh is 4x-8x more expensive, and king moves are common enough
// in a real search that this becomes a first-order cost.
//
// HOW. For each (perspective, king square) the cache keeps the accumulator it
// last produced together with the exact piece placement that produced it. A
// refresh then applies only the difference between the cached placement and
// the current one - in a real search, usually a handful of rows rather than 32.
//
// KEYED BY KING SQUARE, NOT BY BUCKET. Two king squares sharing a bucket are
// horizontal mirrors of each other (the bucket table is symmetric), and the
// mirror changes Orient(), hence every feature index. Keying by bucket alone
// would mix two different feature spaces into one entry. 64 squares x 2
// perspectives costs 128 entries: 32 KB at FT=128, 256 KB at FT=1024 - per
// thread, which is the correct scope since each search thread owns its own
// evaluator and therefore its own cache.
//
// CORRECTNESS. An entry starts as "the empty board" (accumulator = ftBias,
// placement = all zero), so the very first refresh for a square degenerates to
// exactly the old full rebuild. From then on it is a diff. Either way the
// result is bit-identical to NnueAccumulator.Refresh, which the tests assert
// directly.
public sealed class NnueAccumulatorCache
{
    private const int PieceTypeCount = 6;   // Pawn..King
    private const int PlaneCount = 2 * PieceTypeCount;
    private const int Squares = 64;
    private const int Entries = 2 * Squares; // perspective * king square

    private readonly NnueNetwork _network;

    // Accumulator state per entry, and the piece placement that produced it.
    private readonly short[][] _values = new short[Entries][];
    private readonly ulong[][] _placement = new ulong[Entries][];
    // Psqt head lane per entry, diffed exactly like the rows above. Zero for
    // an empty board, so zero-initialisation IS the honest starting point.
    private readonly int[][] _psqt = new int[Entries][];

    public NnueAccumulatorCache(NnueNetwork network)
    {
        _network = network;
        for (int i = 0; i < Entries; i++)
        {
            // ftBias + no pieces == the accumulator of an empty board, which is
            // the honest starting point for a diff.
            _values[i] = new short[network.FtOutputs];
            Array.Copy(network.FtBias, _values[i], network.FtOutputs);
            _placement[i] = new ulong[PlaneCount];
            _psqt[i] = new int[NnueAccumulator.MaxPsqtBuckets];
        }
    }

    // Rebuilds 'perspective' of 'target' from the cache and leaves the cache
    // holding the current position, ready for the next refresh of this square.
    public void Refresh(NnueAccumulator target, Board board, Color perspective)
    {
        int kingSquare = board.KingSquare(perspective);
        int entry = ((int)perspective * Squares) + kingSquare;

        short[] values = _values[entry];
        ulong[] placement = _placement[entry];
        int[] psqt = _psqt[entry];
        bool wasPopulated = false;
        int touched = 0;

        for (int plane = 0; plane < PlaneCount; plane++)
        {
            var color = (Color)(plane / PieceTypeCount);
            var pieceType = (PieceType)(plane % PieceTypeCount);

            ulong current = board.Pieces(color, pieceType);
            ulong cached = placement[plane];
            if (current == cached)
            {
                wasPopulated |= cached != 0;
                continue;
            }
            wasPopulated |= cached != 0;

            // Pieces that appeared since this entry was last written.
            ulong added = current & ~cached;
            while (added != 0)
            {
                int square = Bitboard.PopLsb(ref added);
                AddRow(values, psqt, NnueFeatureIndex.Index(perspective, kingSquare, color, pieceType, square));
                touched++;
            }

            // Pieces that disappeared.
            ulong removed = cached & ~current;
            while (removed != 0)
            {
                int square = Bitboard.PopLsb(ref removed);
                SubtractRow(values, psqt, NnueFeatureIndex.Index(perspective, kingSquare, color, pieceType, square));
                touched++;
            }

            placement[plane] = current;
        }

        Array.Copy(values, target.Values[(int)perspective], values.Length);
        if (_network.PsqtBuckets > 0)
            Array.Copy(psqt, 0, target.Psqt,
                       (int)perspective * NnueAccumulator.MaxPsqtBuckets,
                       NnueAccumulator.MaxPsqtBuckets);
        target.Valid[(int)perspective] = true;
        target.Computed[(int)perspective] = true;

        NnueProfiling.CountRefresh(wasPopulated, touched);
    }

    // Same ref-based loads as NnueAccumulator: the array-indexed Vector<T>
    // constructor bounds-checks every iteration and the JIT does not reliably
    // hoist it, which the v4.0.0 profile caught costing ~100x the arithmetic.
    private void AddRow(short[] values, int[] psqt, int featureIndex)
    {
        short[] weights = _network.FtWeights;
        int ftOut = _network.FtOutputs;
        for (int b = 0; b < _network.PsqtBuckets; b++)
            psqt[b] += _network.PsqtWeights![featureIndex * _network.PsqtBuckets + b];

        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short v = ref MemoryMarshal.GetArrayDataReference(values);
            ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
            nuint row = (nuint)featureIndex * (nuint)ftOut;
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                (Vector256.LoadUnsafe(ref v, i) + Vector256.LoadUnsafe(ref w, row + i))
                    .StoreUnsafe(ref v, i);
            return;
        }

        int offset = featureIndex * ftOut;
        int width = Vector<short>.Count;
        int n = values.Length;
        int k = 0;
        for (; k <= n - width; k += width)
            (new Vector<short>(values, k) + new Vector<short>(weights, offset + k)).CopyTo(values, k);
        for (; k < n; k++)
            values[k] += weights[offset + k];
    }

    private void SubtractRow(short[] values, int[] psqt, int featureIndex)
    {
        short[] weights = _network.FtWeights;
        int ftOut = _network.FtOutputs;
        for (int b = 0; b < _network.PsqtBuckets; b++)
            psqt[b] -= _network.PsqtWeights![featureIndex * _network.PsqtBuckets + b];

        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short v = ref MemoryMarshal.GetArrayDataReference(values);
            ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
            nuint row = (nuint)featureIndex * (nuint)ftOut;
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                (Vector256.LoadUnsafe(ref v, i) - Vector256.LoadUnsafe(ref w, row + i))
                    .StoreUnsafe(ref v, i);
            return;
        }

        int offset = featureIndex * ftOut;
        int width = Vector<short>.Count;
        int n = values.Length;
        int k = 0;
        for (; k <= n - width; k += width)
            (new Vector<short>(values, k) - new Vector<short>(weights, offset + k)).CopyTo(values, k);
        for (; k < n; k++)
            values[k] -= weights[offset + k];
    }
}
