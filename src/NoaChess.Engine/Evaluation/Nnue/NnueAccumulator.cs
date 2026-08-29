using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// One accumulator: the pre-activation output of the feature transformer for
// BOTH perspectives. This is the "efficiently updatable" part of NNUE - when
// a move changes 2-4 features, the accumulator is patched by adding and
// subtracting a couple of weight rows instead of recomputing the sum of ~30.
public sealed class NnueAccumulator
{

    // Scratch for the threat half of a full refresh. See its use below.
    [ThreadStatic]
    private static int[]? ThreatScratch;

    // [perspective, ftOutputs] - White = 0, Black = 1.
    public readonly short[][] Values;

    // Psqt head lane, [perspective * MaxPsqtBuckets + bucket]. Fixed-size so
    // no constructor call site changes; 64 bytes per accumulator is noise
    // against the 2 x ftOutputs shorts above. All zero for nets without a
    // psqt head, and every update below is gated so those nets never touch it.
    public const int MaxPsqtBuckets = 8;
    public readonly int[] Psqt = new int[2 * MaxPsqtBuckets];

    // A perspective becomes invalid when ITS king moves (every feature of the
    // perspective is king-relative, so all of them change at once) and must
    // be refreshed from scratch.
    public readonly bool[] Valid = new bool[2];

    // Whether Values[perspective] actually holds this position's accumulator.
    // The stack is LAZY: a push records the update and clears this, and the
    // numbers are only materialised when an evaluation asks for them. Valid and
    // Computed are independent - a perspective can be valid (no king move) and
    // still uncomputed (nobody has needed it yet).
    public readonly bool[] Computed = new bool[2];

    public NnueAccumulator(int ftOutputs)
    {
        Values = [new short[ftOutputs], new short[ftOutputs]];
    }

    // Full recomputation of one perspective from the board (the reference
    // path; also used after king moves). Incremental updates must always
    // produce results identical to this.
    public void Refresh(NnueNetwork network, Board board, Color perspective)
    {
        short[] values = Values[(int)perspective];
        Array.Copy(network.FtBias, values, values.Length);
        if (network.PsqtBuckets > 0)
            Array.Clear(Psqt, (int)perspective * MaxPsqtBuckets, MaxPsqtBuckets);

        Span<int> features = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        int count = NnueFeatureIndex.ActiveFeatures(board, perspective, features);

        for (int i = 0; i < count; i++)
            AddFeature(network, perspective, features[i]);

        // Threat features sum into the SAME accumulator, which is why there is
        // one bias and not two: the reference does exactly this, its
        // InputDimensions being the HalfKA size plus the threat size over one
        // transformer output.
        //
        // FULL REFRESH ONLY, and knowingly so. This costs about 1600-1900 ns per
        // perspective against roughly 1000 ns for an entire evaluation, measured
        // in ThreatFeatureCostTests, so a net using it cannot ship. It CAN be
        // measured at fixed nodes, where speed leaves the comparison, and that
        // is the whole plan: prove the features are worth it on a slow build,
        // then write the incremental update - which has to track discovered
        // threats through opening and closing slider rays - only if they are.
        if (network.UsesThreats)
        {
            // Per-thread scratch, NOT a stackalloc. The bound is 512 since a
            // dense position overflowed 128 and crashed seven games, and C#
            // zeroes a stackalloc, so this was memsetting two kilobytes every
            // time a king move forced a rebuild. The sibling buffer in
            // CompleteThreatDelta cost 96.6% of the search for the same reason;
            // this is the same mistake in the path that call falls back to.
            //
            // ThreadStatic rather than a field on the accumulator: there are
            // MaxPly accumulators per stack and only one is refreshing at a
            // time, so one buffer per worker beats 256 of them. It is written
            // by ActiveFeatures up to threatCount and read only that far, so it
            // never needs to start clean.
            Span<int> threats = ThreatScratch ??= new int[ThreatFeatureIndex.MaxActiveFeatures];
            int threatCount = ThreatFeatureIndex.ActiveFeatures(board, perspective, threats);

            // Probe only, off in normal play: what a finny table for threats
            // would have saved here. See NnueProfiling.CountThreatRefresh for
            // what it is deciding and why counting beats arguing about it.
            if (NnueProfiling.Enabled)
            {
                Span<byte> signature = stackalloc byte[64];
                for (int sq = 0; sq < 64; sq++)
                {
                    PieceType type = board.PieceTypeAt(sq);
                    signature[sq] = type == PieceType.None
                        ? (byte)12
                        : (byte)((int)board.ColorAt(sq) * 6 + (int)type);
                }
                NnueProfiling.CountThreatRefresh((int)perspective, board.KingSquare(perspective),
                                                 signature, threats[..threatCount]);
            }

            short[] weights = network.ThreatWeights!;
            int width = network.FtOutputs;
            for (int i = 0; i < threatCount; i++)
            {
                int off = threats[i] * width;
                for (int j = 0; j < width; j++)
                    values[j] += weights[off + j];
            }
        }

        Valid[(int)perspective] = true;
        Computed[(int)perspective] = true;
    }

    // Both perspectives at once. Kept for the `nnueprofile` microbenchmark,
    // which prices this copy in isolation; the search takes the lazy path below.
    public void CopyFrom(NnueAccumulator other)
    {
        NnueProfiling.CountCopyFrom();
        Array.Copy(other.Values[0], Values[0], Values[0].Length);
        Array.Copy(other.Values[1], Values[1], Values[1].Length);
        Array.Copy(other.Psqt, Psqt, Psqt.Length);
        Valid[0] = other.Valid[0];
        Valid[1] = other.Valid[1];
        Computed[0] = other.Computed[0];
        Computed[1] = other.Computed[1];
    }

    // One perspective only: what the lazy stack needs when it materialises a
    // level from its nearest computed ancestor. Half the traffic of CopyFrom,
    // and paid only for the perspective an evaluation actually asked for.
    public void CopyPerspectiveFrom(NnueAccumulator other, Color perspective)
    {
        NnueProfiling.CountPerspectiveCopy();
        int p = (int)perspective;
        Array.Copy(other.Values[p], Values[p], Values[p].Length);
        Array.Copy(other.Psqt, p * MaxPsqtBuckets, Psqt, p * MaxPsqtBuckets, MaxPsqtBuckets);
    }

    // ---- Feature-transformer row updates: the hottest code in NNUE play ----
    //
    // A weight row is FtOutputs int16 wide and is added to / subtracted from the
    // accumulator several times per search node. The v4.0.0 profile measured
    // this family at 73.8% of all NNUE work - more than the L1 dot product by a
    // factor of three, and the exact opposite of what the old comment in
    // NnueInference asserted.
    //
    // WHY ref-BASED LOADS AND NOT new Vector<short>(array, index). The array
    // constructor bounds-checks on every call and the JIT frequently fails to
    // hoist those checks out of the loop, which is why the profile showed
    // MoveFeature at ~387 ns for what is 8 vector additions of real work.
    // Vector256.LoadUnsafe over a ref obtained once is the same idiom the
    // inference kernel already uses, and it removes the per-iteration check.
    // The portable Vector<T> path is kept for non-AVX2 hardware; both produce
    // identical results, which the incremental-vs-refresh tests assert.

    // Threat rows, added and removed one at a time.
    //
    // The threat transformer sums into the SAME accumulator as HalfKA, just from
    // a different weight table, so an incremental threat update is the identical
    // operation over ThreatWeights instead of FtWeights. Kept as its own pair
    // rather than a weight-array parameter on AddFeature because that method is
    // the hottest in the engine and a parameter it does not need is a parameter
    // the JIT has to carry.
    public void AddThreat(NnueNetwork network, Color perspective, int featureIndex)
        => ApplyThreat(network, perspective, featureIndex, add: true);

    public void SubtractThreat(NnueNetwork network, Color perspective, int featureIndex)
        => ApplyThreat(network, perspective, featureIndex, add: false);

    private void ApplyThreat(NnueNetwork network, Color perspective, int featureIndex, bool add)
    {
        NnueProfiling.CountAccumulatorUpdate();
        NnueProfiling.CountThreatRow();
        short[] values = Values[(int)perspective];
        short[] weights = network.ThreatWeights!;
        int ftOut = network.FtOutputs;

        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short v = ref MemoryMarshal.GetArrayDataReference(values);
            ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
            nuint row = (nuint)featureIndex * (nuint)ftOut;
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
            {
                Vector256<short> cur = Vector256.LoadUnsafe(ref v, i);
                Vector256<short> delta = Vector256.LoadUnsafe(ref w, row + i);
                (add ? cur + delta : cur - delta).StoreUnsafe(ref v, i);
            }
            return;
        }

        int offset = featureIndex * ftOut;
        for (int j = 0; j < ftOut; j++)
            values[j] = (short)(add ? values[j] + weights[offset + j]
                                    : values[j] - weights[offset + j]);
    }

    // The psqt half of one feature update. Rides along every row operation
    // below: a lane that can be updated from one place and forgotten in
    // another is exactly the silent-corruption shape this module has already
    // paid for twice, so the row op and the lane share the entry point.
    private void PsqtApply(NnueNetwork network, Color perspective, int featureIndex, int sign)
    {
        int buckets = network.PsqtBuckets;
        int[] pw = network.PsqtWeights!;
        int off = featureIndex * buckets;
        int lane = (int)perspective * MaxPsqtBuckets;
        for (int b = 0; b < buckets; b++)
            Psqt[lane + b] += sign * pw[off + b];
    }

    public void AddFeature(NnueNetwork network, Color perspective, int featureIndex)
    {
        NnueProfiling.CountAccumulatorUpdate();
        if (network.PsqtBuckets > 0)
            PsqtApply(network, perspective, featureIndex, +1);
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int ftOut = network.FtOutputs;

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

        AddFeaturePortable(values, weights, featureIndex * ftOut);
    }

    public void SubtractFeature(NnueNetwork network, Color perspective, int featureIndex)
    {
        NnueProfiling.CountAccumulatorUpdate();
        if (network.PsqtBuckets > 0)
            PsqtApply(network, perspective, featureIndex, -1);
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int ftOut = network.FtOutputs;

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

        SubtractFeaturePortable(values, weights, featureIndex * ftOut);
    }

    // Fused "a piece left removeIndex and arrived at addIndex": one pass over
    // the accumulator instead of a SubtractFeature + AddFeature pair, halving
    // the load/store traffic on 'values'. Every non-king move does exactly this
    // for both perspectives, so it is the single most-executed update - and the
    // largest single line in the cost profile.
    public void MoveFeature(NnueNetwork network, Color perspective, int removeIndex, int addIndex)
    {
        NnueProfiling.CountFusedMove();
        if (network.PsqtBuckets > 0)
        {
            PsqtApply(network, perspective, addIndex, +1);
            PsqtApply(network, perspective, removeIndex, -1);
        }
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int ftOut = network.FtOutputs;

        if (Avx2.IsSupported && ftOut % Vector256<short>.Count == 0)
        {
            ref short v = ref MemoryMarshal.GetArrayDataReference(values);
            ref short w = ref MemoryMarshal.GetArrayDataReference(weights);
            nuint addRow = (nuint)addIndex * (nuint)ftOut;
            nuint removeRow = (nuint)removeIndex * (nuint)ftOut;
            for (nuint i = 0; i < (nuint)ftOut; i += (nuint)Vector256<short>.Count)
                (Vector256.LoadUnsafe(ref v, i)
                    + Vector256.LoadUnsafe(ref w, addRow + i)
                    - Vector256.LoadUnsafe(ref w, removeRow + i))
                    .StoreUnsafe(ref v, i);
            return;
        }

        int add = addIndex * ftOut;
        int remove = removeIndex * ftOut;
        int width = Vector<short>.Count;
        int n = values.Length;
        int j = 0;
        for (; j <= n - width; j += width)
            (new Vector<short>(values, j)
                + new Vector<short>(weights, add + j)
                - new Vector<short>(weights, remove + j)).CopyTo(values, j);
        for (; j < n; j++)
            values[j] += (short)(weights[add + j] - weights[remove + j]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddFeaturePortable(short[] values, short[] weights, int row)
    {
        int width = Vector<short>.Count;
        int n = values.Length;
        int i = 0;
        for (; i <= n - width; i += width)
            (new Vector<short>(values, i) + new Vector<short>(weights, row + i)).CopyTo(values, i);
        for (; i < n; i++)
            values[i] += weights[row + i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SubtractFeaturePortable(short[] values, short[] weights, int row)
    {
        int width = Vector<short>.Count;
        int n = values.Length;
        int i = 0;
        for (; i <= n - width; i += width)
            (new Vector<short>(values, i) - new Vector<short>(weights, row + i)).CopyTo(values, i);
        for (; i < n; i++)
            values[i] -= weights[row + i];
    }
}
