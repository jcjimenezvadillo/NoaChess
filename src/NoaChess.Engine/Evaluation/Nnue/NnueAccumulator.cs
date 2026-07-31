using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// One accumulator: the pre-activation output of the feature transformer for
// BOTH perspectives. This is the "efficiently updatable" part of NNUE — when
// a move changes 2-4 features, the accumulator is patched by adding and
// subtracting a couple of weight rows instead of recomputing the sum of ~30.
public sealed class NnueAccumulator
{
    // [perspective, ftOutputs] — White = 0, Black = 1.
    public readonly short[][] Values;

    // A perspective becomes invalid when ITS king moves (every feature of the
    // perspective is king-relative, so all of them change at once) and must
    // be refreshed from scratch.
    public readonly bool[] Valid = new bool[2];

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

        Span<int> features = stackalloc int[NnueFeatureIndex.MaxActiveFeatures];
        int count = NnueFeatureIndex.ActiveFeatures(board, perspective, features);

        for (int i = 0; i < count; i++)
            AddFeature(network, perspective, features[i]);

        Valid[(int)perspective] = true;
    }

    public void CopyFrom(NnueAccumulator other)
    {
        NnueProfiling.CountCopyFrom();
        Array.Copy(other.Values[0], Values[0], Values[0].Length);
        Array.Copy(other.Values[1], Values[1], Values[1].Length);
        Valid[0] = other.Valid[0];
        Valid[1] = other.Valid[1];
    }

    // ---- Feature-transformer row updates: the hottest code in NNUE play ----
    //
    // A weight row is FtOutputs int16 wide and is added to / subtracted from the
    // accumulator several times per search node. The v4.0.0 profile measured
    // this family at 73.8% of all NNUE work — more than the L1 dot product by a
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

    public void AddFeature(NnueNetwork network, Color perspective, int featureIndex)
    {
        NnueProfiling.CountAccumulatorUpdate();
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
    // for both perspectives, so it is the single most-executed update — and the
    // largest single line in the cost profile.
    public void MoveFeature(NnueNetwork network, Color perspective, int removeIndex, int addIndex)
    {
        NnueProfiling.CountFusedMove();
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
