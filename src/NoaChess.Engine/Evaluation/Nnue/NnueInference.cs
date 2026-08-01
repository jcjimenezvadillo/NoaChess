using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace NoaChess.Engine.Evaluation.Nnue;

// Forward pass over the accumulators. Two architectures, each with a readable
// scalar reference and a SIMD fast path; the tests assert every pair produces
// identical results.
//
//   ARCH 1 (legacy): L1 weights int16, activations clamped to [0, QA=255].
//                    SIMD path is VPMADDWD.
//   ARCH 2 (v4.0.0): L1 weights int8, activations packed to unsigned bytes
//                    clamped to [0, QA<=127]. SIMD path is VPMADDUBSW+VPMADDWD.
//
// All math is integer (see NnueNetwork for the quantization contract).
//
// COST NOTE (v4.0.0, replaces an assertion that was wrong). This file used to
// claim the L1 dot product was "THE cost of NNUE eval". At FT=128 / L1=32 the
// product is 32 x 256 = 8,192 MACs, about 512 AVX2 instructions per evaluation
// — far too small to dominate at 446k NPS. The real distribution is measured by
// the `nnueprofile` command; do not re-derive it from intuition.
public static class NnueInference
{
    // Chosen once at startup: Vector<short> maps to AVX2 (16 lanes) or SSE2
    // (8 lanes) on x64, AdvSimd on ARM64.
    public static readonly bool SimdAvailable =
        Vector.IsHardwareAccelerated && Vector<short>.Count <= 32;

    // Evaluates from the side to move's point of view, in centipawns.
    // 'stmAccumulator'/'oppAccumulator' are the feature-transformer outputs
    // for the side to move and the opponent (already king-refresh-valid).
    // 'bucket' selects the head replica (arch 3). It is always 0 for the
    // unbucketed architectures, which makes every offset below vanish.
    public static int Evaluate(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                               int bucket = 0)
        => SimdAvailable
            ? EvaluateSimd(net, stmAccumulator, oppAccumulator, bucket)
            : EvaluateScalar(net, stmAccumulator, oppAccumulator, bucket);

    // ---- Shared tail: hidden activations -> output -> centipawns ----
    // Identical for every architecture; only the way 'hidden' was produced
    // differs. Kept in one place so the paths cannot drift apart.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FinishOutput(NnueNetwork net, ReadOnlySpan<int> hidden, int bucket)
    {
        int qa = net.QA;
        int headOffset = bucket * net.L1Outputs;
        long output = net.OutBias[bucket];
        for (int o = 0; o < net.L1Outputs; o++)
        {
            int a2 = Math.Clamp(hidden[o] / net.QB, 0, qa);
            output += net.OutWeights[headOffset + o] * (long)a2;
        }
        return (int)(output * net.OutputScale / ((long)qa * net.QB));
    }

    public static int EvaluateScalar(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                                     int bucket = 0)
    {
        NnueProfiling.CountEvaluation();

        int ftOut = net.FtOutputs;
        int qa = net.QA;
        int inputs = 2 * ftOut;
        // Head arrays are bucket-major; one bucket's L1 block is L1Outputs rows.
        int weightBase = bucket * net.L1Outputs * inputs;
        int biasBase = bucket * net.L1Outputs;

        Span<int> hidden = stackalloc int[net.L1Outputs];

        if (net.UsesInt8L1)
        {
            sbyte[] weights = net.L1WeightsI8!;
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int sum = net.L1Bias[biasBase + o];
                int row = weightBase + o * inputs;
                for (int i = 0; i < ftOut; i++)
                    sum += weights[row + i] * Math.Clamp((int)stmAccumulator[i], 0, qa);
                for (int i = 0; i < ftOut; i++)
                    sum += weights[row + ftOut + i] * Math.Clamp((int)oppAccumulator[i], 0, qa);
                hidden[o] = sum;
            }
        }
        else
        {
            short[] weights = net.L1Weights!;
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int sum = net.L1Bias[biasBase + o];
                int row = weightBase + o * inputs;
                for (int i = 0; i < ftOut; i++)
                    sum += weights[row + i] * Math.Clamp((int)stmAccumulator[i], 0, qa);
                for (int i = 0; i < ftOut; i++)
                    sum += weights[row + ftOut + i] * Math.Clamp((int)oppAccumulator[i], 0, qa);
                hidden[o] = sum;
            }
        }

        return FinishOutput(net, hidden, bucket);
    }

    public static int EvaluateSimd(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                                   int bucket = 0)
    {
        NnueProfiling.CountEvaluation();

        return net.UsesInt8L1
            ? EvaluateInt8(net, stmAccumulator, oppAccumulator, bucket)
            : EvaluateInt16(net, stmAccumulator, oppAccumulator, bucket);
    }

    // ---- ARCH 2: int8 L1, VPMADDUBSW + VPMADDWD ----
    //
    // Activations are packed into UNSIGNED bytes and weights are SIGNED bytes,
    // which is exactly the operand shape VPMADDUBSW wants. It multiplies 32
    // pairs and horizontally adds them into 16 int16 lanes; VPMADDWD then folds
    // those into 8 int32. Two instructions per 32 elements against VPMADDWD's
    // one per 16, but each covers twice the data and reads half the weight
    // bytes — the weight stream is the part that does not fit in cache.
    //
    // SATURATION IS IMPOSSIBLE HERE, not merely unlikely: the int16 lane holds
    // a0*w0 + a1*w1 with a in [0,127] and w in [-127,127], so the magnitude is
    // at most 2*127*127 = 32,258 against an int16 limit of 32,767. The loader
    // refuses any arch-2 model with QA > 127, which is what makes that bound
    // hold for every possible position rather than for the ones we tested.
    private static int EvaluateInt8(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                                    int bucket)
    {
        int ftOut = net.FtOutputs;
        int inputs = 2 * ftOut;
        sbyte[] weights = net.L1WeightsI8!;
        // Only THIS bucket's block is read; the other buckets are never touched,
        // which is why adding buckets costs weight memory but not evaluation time.
        int weightBase = bucket * net.L1Outputs * inputs;
        int biasBase = bucket * net.L1Outputs;

        Span<byte> act = stackalloc byte[inputs];
        Span<int> hidden = stackalloc int[net.L1Outputs];

        if (Avx2.IsSupported && ftOut % 32 == 0)
        {
            PackActivationsAvx2(stmAccumulator, oppAccumulator, act, net.QA, ftOut);

            ref byte actRef = ref MemoryMarshal.GetReference(act);
            ref sbyte wRef = ref MemoryMarshal.GetArrayDataReference(weights);
            var ones = Vector256.Create((short)1);

            for (int o = 0; o < net.L1Outputs; o++)
            {
                nuint row = (nuint)(weightBase + o * inputs);
                var accum = Vector256<int>.Zero;
                for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<byte>.Count)
                {
                    var a = Vector256.LoadUnsafe(ref actRef, i);
                    var w = Vector256.LoadUnsafe(ref wRef, row + i);
                    // VPMADDUBSW: u8 x i8 -> i16 pairs, then VPMADDWD -> i32.
                    var products = Avx2.MultiplyAddAdjacent(a, w);
                    accum = Avx2.Add(accum, Avx2.MultiplyAddAdjacent(products, ones));
                }
                hidden[o] = net.L1Bias[biasBase + o] + Vector256.Sum(accum);
            }
        }
        else
        {
            // Portable fallback: same arithmetic, no packing tricks. Used on
            // non-AVX2 hardware and for widths that are not a multiple of 32.
            PackActivationsScalar(stmAccumulator, oppAccumulator, act, net.QA, ftOut);
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int sum = net.L1Bias[biasBase + o];
                int row = weightBase + o * inputs;
                for (int i = 0; i < inputs; i++)
                    sum += weights[row + i] * act[i];
                hidden[o] = sum;
            }
        }

        return FinishOutput(net, hidden, bucket);
    }

    // Clamps both accumulators to [0, QA] and packs them into one contiguous
    // [stm | opp] byte buffer.
    //
    // PackUnsignedSaturate works per 128-bit lane, so packing shorts a[0..16)
    // and b[0..16) yields the byte order a[0..8) b[0..8) a[8..16) b[8..16).
    // Permute4x64 with control 0xD8 (blocks 0,2,1,3) restores the linear order
    // the weight rows are stored in. Getting this wrong would pair every
    // activation with the wrong weight, so it is asserted by the parity test
    // against the scalar path rather than trusted.
    private static void PackActivationsAvx2(
        short[] stm, short[] opp, Span<byte> act, int qa, int ftOut)
    {
        ref short stmRef = ref MemoryMarshal.GetArrayDataReference(stm);
        ref short oppRef = ref MemoryMarshal.GetArrayDataReference(opp);
        ref byte actRef = ref MemoryMarshal.GetReference(act);

        var zero = Vector256<short>.Zero;
        var qaVec = Vector256.Create((short)qa);

        for (int i = 0; i < ftOut; i += 32)
        {
            PackBlock(ref stmRef, ref actRef, (nuint)i, (nuint)i, zero, qaVec);
            PackBlock(ref oppRef, ref actRef, (nuint)i, (nuint)(ftOut + i), zero, qaVec);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PackBlock(ref short src, ref byte dst, nuint srcIndex, nuint dstIndex,
                                  Vector256<short> zero, Vector256<short> qaVec)
    {
        var lo = Vector256.LoadUnsafe(ref src, srcIndex);
        var hi = Vector256.LoadUnsafe(ref src, srcIndex + 16);
        lo = Avx2.Min(Avx2.Max(lo, zero), qaVec);
        hi = Avx2.Min(Avx2.Max(hi, zero), qaVec);
        var packed = Avx2.PackUnsignedSaturate(lo, hi);
        packed = Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsByte();
        packed.StoreUnsafe(ref dst, dstIndex);
    }

    private static void PackActivationsScalar(
        short[] stm, short[] opp, Span<byte> act, int qa, int ftOut)
    {
        for (int i = 0; i < ftOut; i++)
            act[i] = (byte)Math.Clamp((int)stm[i], 0, qa);
        for (int i = 0; i < ftOut; i++)
            act[ftOut + i] = (byte)Math.Clamp((int)opp[i], 0, qa);
    }

    // ---- ARCH 1: int16 L1, VPMADDWD ----
    private static int EvaluateInt16(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                                     int bucket)
    {
        int ftOut = net.FtOutputs;
        int qa = net.QA;
        int lanes = Vector<short>.Count;
        int inputs = 2 * ftOut;

        var zero = Vector<short>.Zero;
        var qaVec = new Vector<short>((short)qa);

        // Clip the two accumulators once into a contiguous [stm | opp] buffer.
        // The clipped result is identical for every output, so computing it a
        // single time lets each output run a plain dot product over it.
        Span<short> act = stackalloc short[inputs];
        for (int i = 0; i < ftOut; i += lanes)
        {
            Vector.Min(Vector.Max(new Vector<short>(stmAccumulator, i), zero), qaVec).CopyTo(act[i..]);
            Vector.Min(Vector.Max(new Vector<short>(oppAccumulator, i), zero), qaVec).CopyTo(act[(ftOut + i)..]);
        }

        Span<int> hidden = stackalloc int[net.L1Outputs];
        short[] l1Weights = net.L1Weights!;
        int weightBase = bucket * net.L1Outputs * inputs;
        int biasBase = bucket * net.L1Outputs;
        if (Avx2.IsSupported && inputs % Vector256<short>.Count == 0)
        {
            ref short actRef = ref MemoryMarshal.GetReference(act);
            ref short wRef = ref MemoryMarshal.GetArrayDataReference(l1Weights);
            for (int o = 0; o < net.L1Outputs; o++)
            {
                nuint row = (nuint)(weightBase + o * inputs);
                var accum = Vector256<int>.Zero;
                for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<short>.Count)
                {
                    var a = Vector256.LoadUnsafe(ref actRef, i);
                    var w = Vector256.LoadUnsafe(ref wRef, row + i);
                    accum = Avx2.Add(accum, Avx2.MultiplyAddAdjacent(a, w));
                }
                hidden[o] = net.L1Bias[biasBase + o] + Vector256.Sum(accum);
            }
        }
        else
        {
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int row = weightBase + o * inputs;
                var accum = Vector<int>.Zero;
                for (int i = 0; i < inputs; i += lanes)
                {
                    var a = new Vector<short>(act[i..]);
                    var w = new Vector<short>(l1Weights, row + i);
                    Vector.Widen(a, out Vector<int> aLo, out Vector<int> aHi);
                    Vector.Widen(w, out Vector<int> wLo, out Vector<int> wHi);
                    accum += aLo * wLo + aHi * wHi;
                }
                hidden[o] = net.L1Bias[biasBase + o] + Vector.Sum(accum);
            }
        }

        return FinishOutput(net, hidden, bucket);
    }
}
