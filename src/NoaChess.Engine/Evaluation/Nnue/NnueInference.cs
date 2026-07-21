using System.Numerics;

namespace NoaChess.Engine.Evaluation.Nnue;

// Forward pass over the accumulators. Two implementations:
// - Scalar: the readable reference; correctness lives here.
// - SIMD (Vector<T>): the fast path, selected at startup when hardware
//   acceleration exists. Tests assert both produce identical results.
//
// All math is integer (see NnueNetwork for the quantization contract).
public static class NnueInference
{
    // Chosen once at startup: Vector<short> maps to AVX2 (16 lanes) or SSE2
    // (8 lanes) on x64, AdvSimd on ARM64.
    public static readonly bool SimdAvailable =
        Vector.IsHardwareAccelerated && Vector<short>.Count <= 32;

    // Evaluates from the side to move's point of view, in centipawns.
    // 'stmAccumulator'/'oppAccumulator' are the feature-transformer outputs
    // for the side to move and the opponent (already king-refresh-valid).
    public static int Evaluate(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator)
        => SimdAvailable
            ? EvaluateSimd(net, stmAccumulator, oppAccumulator)
            : EvaluateScalar(net, stmAccumulator, oppAccumulator);

    public static int EvaluateScalar(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator)
    {
        int ftOut = net.FtOutputs;
        int qa = net.QA;

        // Hidden layer: h[o] = bias + dot(l1Row, clipped(concat(stm, opp))).
        Span<int> hidden = stackalloc int[net.L1Outputs];
        for (int o = 0; o < net.L1Outputs; o++)
        {
            int sum = net.L1Bias[o];
            int row = o * 2 * ftOut;
            for (int i = 0; i < ftOut; i++)
            {
                int a = Math.Clamp((int)stmAccumulator[i], 0, qa);
                sum += net.L1Weights[row + i] * a;
            }
            for (int i = 0; i < ftOut; i++)
            {
                int a = Math.Clamp((int)oppAccumulator[i], 0, qa);
                sum += net.L1Weights[row + ftOut + i] * a;
            }
            hidden[o] = sum;
        }

        // Output layer over the clipped hidden activations.
        long output = net.OutBias;
        for (int o = 0; o < net.L1Outputs; o++)
        {
            int a2 = Math.Clamp(hidden[o] / net.QB, 0, qa);
            output += net.OutWeights[o] * (long)a2;
        }

        return (int)(output * net.OutputScale / ((long)qa * net.QB));
    }

    public static int EvaluateSimd(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator)
    {
        int ftOut = net.FtOutputs;
        int qa = net.QA;
        int lanes = Vector<short>.Count;

        var zero = Vector<short>.Zero;
        var qaVec = new Vector<short>((short)qa);

        Span<int> hidden = stackalloc int[net.L1Outputs];

        for (int o = 0; o < net.L1Outputs; o++)
        {
            int row = o * 2 * ftOut;
            var accum = Vector<int>.Zero;

            // First half: side to move accumulator.
            for (int i = 0; i < ftOut; i += lanes)
            {
                var a = Vector.Min(Vector.Max(new Vector<short>(stmAccumulator, i), zero), qaVec);
                var w = new Vector<short>(net.L1Weights, row + i);
                // Widening multiply keeps the products in int32 lanes.
                Vector.Widen(a, out Vector<int> aLo, out Vector<int> aHi);
                Vector.Widen(w, out Vector<int> wLo, out Vector<int> wHi);
                accum += aLo * wLo + aHi * wHi;
            }
            // Second half: opponent accumulator.
            for (int i = 0; i < ftOut; i += lanes)
            {
                var a = Vector.Min(Vector.Max(new Vector<short>(oppAccumulator, i), zero), qaVec);
                var w = new Vector<short>(net.L1Weights, row + ftOut + i);
                Vector.Widen(a, out Vector<int> aLo, out Vector<int> aHi);
                Vector.Widen(w, out Vector<int> wLo, out Vector<int> wHi);
                accum += aLo * wLo + aHi * wHi;
            }

            hidden[o] = net.L1Bias[o] + Vector.Sum(accum);
        }

        long output = net.OutBias;
        for (int o = 0; o < net.L1Outputs; o++)
        {
            int a2 = Math.Clamp(hidden[o] / net.QB, 0, qa);
            output += net.OutWeights[o] * (long)a2;
        }

        return (int)(output * net.OutputScale / ((long)qa * net.QB));
    }
}
