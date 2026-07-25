using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        int inputs = 2 * ftOut;

        var zero = Vector<short>.Zero;
        var qaVec = new Vector<short>((short)qa);

        // Clip the two accumulators once into a contiguous [stm | opp] buffer.
        // The old code re-clipped both accumulators inside every L1-output loop
        // (L1Outputs times over), which dominated the eval; the clipped result
        // is identical for every output, so compute it a single time and let
        // each output run a plain dot product over it.
        Span<short> act = stackalloc short[inputs];
        for (int i = 0; i < ftOut; i += lanes)
        {
            Vector.Min(Vector.Max(new Vector<short>(stmAccumulator, i), zero), qaVec).CopyTo(act[i..]);
            Vector.Min(Vector.Max(new Vector<short>(oppAccumulator, i), zero), qaVec).CopyTo(act[(ftOut + i)..]);
        }

        // Hidden layer: hidden[o] = bias[o] + dot(L1Weights[o], act). This dot
        // product (L1Outputs x 2*FtOutputs int16 MACs) is THE cost of NNUE
        // eval — the accumulator update is already faster than a classical
        // eval, so all the speed lives here. On AVX2 use VPMADDWD
        // (MultiplyAddAdjacent): one instruction multiplies 16 int16 pairs and
        // horizontally adds them into 8 int32, no saturation (int16*int16 sums
        // of two fit int32) — ~4x fewer instructions than widen+multiply, and
        // bit-identical to the scalar reference. Falls back to Vector<T>.
        Span<int> hidden = stackalloc int[net.L1Outputs];
        short[] l1Weights = net.L1Weights;
        if (Avx2.IsSupported && inputs % Vector256<short>.Count == 0)
        {
            ref short actRef = ref MemoryMarshal.GetReference(act);
            ref short wRef = ref MemoryMarshal.GetArrayDataReference(l1Weights);
            for (int o = 0; o < net.L1Outputs; o++)
            {
                nuint row = (nuint)(o * inputs);
                var accum = Vector256<int>.Zero;
                for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<short>.Count)
                {
                    var a = Vector256.LoadUnsafe(ref actRef, i);
                    var w = Vector256.LoadUnsafe(ref wRef, row + i);
                    accum = Avx2.Add(accum, Avx2.MultiplyAddAdjacent(a, w));
                }
                hidden[o] = net.L1Bias[o] + Vector256.Sum(accum);
            }
        }
        else
        {
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int row = o * inputs;
                var accum = Vector<int>.Zero;
                for (int i = 0; i < inputs; i += lanes)
                {
                    var a = new Vector<short>(act[i..]);
                    var w = new Vector<short>(l1Weights, row + i);
                    Vector.Widen(a, out Vector<int> aLo, out Vector<int> aHi);
                    Vector.Widen(w, out Vector<int> wLo, out Vector<int> wHi);
                    accum += aLo * wLo + aHi * wHi;
                }
                hidden[o] = net.L1Bias[o] + Vector.Sum(accum);
            }
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
