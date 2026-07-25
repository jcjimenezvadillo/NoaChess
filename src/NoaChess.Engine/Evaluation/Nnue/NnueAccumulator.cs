using System.Numerics;
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
        Array.Copy(other.Values[0], Values[0], Values[0].Length);
        Array.Copy(other.Values[1], Values[1], Values[1].Length);
        Valid[0] = other.Valid[0];
        Valid[1] = other.Valid[1];
    }

    // The feature-transformer weight row for a feature is FtOutputs int16 wide
    // (128+) and is added to / subtracted from the accumulator on every move —
    // several times per search node. A scalar loop is the hottest cost in NNUE
    // play; process it a full SIMD vector at a time. FtOutputs is a multiple of
    // the vector width for the shipped nets, so the scalar tail never runs.
    public void AddFeature(NnueNetwork network, Color perspective, int featureIndex)
    {
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int row = featureIndex * network.FtOutputs;
        int width = Vector<short>.Count;
        int n = values.Length;
        int i = 0;
        for (; i <= n - width; i += width)
            (new Vector<short>(values, i) + new Vector<short>(weights, row + i)).CopyTo(values, i);
        for (; i < n; i++)
            values[i] += weights[row + i];
    }

    public void SubtractFeature(NnueNetwork network, Color perspective, int featureIndex)
    {
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int row = featureIndex * network.FtOutputs;
        int width = Vector<short>.Count;
        int n = values.Length;
        int i = 0;
        for (; i <= n - width; i += width)
            (new Vector<short>(values, i) - new Vector<short>(weights, row + i)).CopyTo(values, i);
        for (; i < n; i++)
            values[i] -= weights[row + i];
    }

    // Fused "a piece left removeIndex and arrived at addIndex": one pass over
    // the accumulator instead of a SubtractFeature + AddFeature pair, halving
    // the load/store traffic on 'values'. Every non-king move does exactly this
    // for both perspectives, so it is the single most-executed update.
    public void MoveFeature(NnueNetwork network, Color perspective, int removeIndex, int addIndex)
    {
        short[] values = Values[(int)perspective];
        short[] weights = network.FtWeights;
        int addRow = addIndex * network.FtOutputs;
        int removeRow = removeIndex * network.FtOutputs;
        int width = Vector<short>.Count;
        int n = values.Length;
        int i = 0;
        for (; i <= n - width; i += width)
            (new Vector<short>(values, i)
                + new Vector<short>(weights, addRow + i)
                - new Vector<short>(weights, removeRow + i)).CopyTo(values, i);
        for (; i < n; i++)
            values[i] += (short)(weights[addRow + i] - weights[removeRow + i]);
    }
}
