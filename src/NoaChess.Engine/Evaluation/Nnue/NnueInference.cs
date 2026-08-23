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
// - far too small to dominate at 446k NPS. The real distribution is measured by
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

    // ---- ARCH 5: pairwise transformer read, squared activations, two hidden
    // layers, and a linear bypass ----
    //
    // THE PAIRWISE DIVISOR IS A SHIFT, AND THAT IS A DEFINITION RATHER THAN AN
    // APPROXIMATION. The natural way to write the product of two activations
    // and stay on the same grid is a0 * a1 / QA, but QA is 127 and dividing a
    // vector of int16 by 127 has no cheap exact SIMD form - the obvious
    // reciprocal-multiply tricks are off by one for inputs of the form
    // 127k - 1, which is not a rare corner but one value in every 127. So the
    // divisor is 128:
    //
    //     pair = (a0 * a1) >> PairShift          PairShift = 7
    //
    // which is one instruction, exact for every input, and bounded by
    // 127*127 >> 7 = 126, so it still packs into an unsigned byte and still
    // satisfies the int16 bound the int8 dot product relies on.
    //
    // The TRAINER mirrors this exactly by folding the same constant into its
    // float activation (x0 * x1 * QA / 128). Both sides then describe the same
    // function rather than two that agree to within a percent, which is the
    // only standard worth holding a quantization contract to.
    public const int PairShift = 7;

    // Both hidden layers emit the square of their clipped activation next to
    // the clipped activation itself, squares FIRST - the same order the
    // reference lays out its concatenation buffer in. Getting the order wrong
    // would pair every value with the wrong output weight, which is why the
    // parity test compares against the trainer instead of against intuition.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DualActivate(ReadOnlySpan<int> pre, Span<byte> act, int qa,
                                     int qb, int qbShift, byte[] squared)
    {
        int n = pre.Length;
        if (qbShift >= 0)
        {
            // The shift and the division disagree only on negative values, and
            // the clamp maps both of those to 0, so this is exact.
            for (int o = 0; o < n; o++)
            {
                int c = Math.Clamp(pre[o] >> qbShift, 0, qa);
                act[o] = squared[c];
                act[n + o] = (byte)c;
            }
            return;
        }

        for (int o = 0; o < n; o++)
        {
            int c = Math.Clamp(pre[o] / qb, 0, qa);
            act[o] = squared[c];
            act[n + o] = (byte)c;
        }
    }

    // acc[j] * acc[j + half], both clipped, for each perspective in turn.
    private static void PairwiseScalar(short[] stm, short[] opp, Span<byte> act, int qa, int half)
    {
        for (int j = 0; j < half; j++)
        {
            int a0 = Math.Clamp((int)stm[j], 0, qa);
            int a1 = Math.Clamp((int)stm[j + half], 0, qa);
            act[j] = (byte)((a0 * a1) >> PairShift);
        }
        for (int j = 0; j < half; j++)
        {
            int b0 = Math.Clamp((int)opp[j], 0, qa);
            int b1 = Math.Clamp((int)opp[j + half], 0, qa);
            act[half + j] = (byte)((b0 * b1) >> PairShift);
        }
    }

    private static void PairwiseAvx2(short[] stm, short[] opp, Span<byte> act, int qa, int half)
    {
        ref short stmRef = ref MemoryMarshal.GetArrayDataReference(stm);
        ref short oppRef = ref MemoryMarshal.GetArrayDataReference(opp);
        ref byte actRef = ref MemoryMarshal.GetReference(act);

        var zero = Vector256<short>.Zero;
        var qaVec = Vector256.Create((short)qa);

        for (int j = 0; j < half; j += 32)
        {
            PairBlock(ref stmRef, ref actRef, (nuint)j, (nuint)half, (nuint)j, zero, qaVec);
            PairBlock(ref oppRef, ref actRef, (nuint)j, (nuint)half, (nuint)(half + j), zero, qaVec);
        }
    }

    // Produces 32 bytes of pairwise output from 64 accumulator entries: two
    // 16-lane products, then the same packus + permute dance the arch 2 packer
    // uses, for the same reason (packus works per 128-bit lane and would
    // interleave the halves without the permute).
    //
    // The int16 product cannot overflow: both operands are clipped to QA <= 127,
    // so the largest value is 16,129 against an int16 limit of 32,767.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PairBlock(ref short src, ref byte dst, nuint index, nuint half,
                                  nuint dstIndex, Vector256<short> zero, Vector256<short> qaVec)
    {
        var loA = Vector256.LoadUnsafe(ref src, index);
        var loB = Vector256.LoadUnsafe(ref src, index + half);
        var hiA = Vector256.LoadUnsafe(ref src, index + 16);
        var hiB = Vector256.LoadUnsafe(ref src, index + half + 16);

        loA = Avx2.Min(Avx2.Max(loA, zero), qaVec);
        loB = Avx2.Min(Avx2.Max(loB, zero), qaVec);
        hiA = Avx2.Min(Avx2.Max(hiA, zero), qaVec);
        hiB = Avx2.Min(Avx2.Max(hiB, zero), qaVec);

        var lo = Avx2.ShiftRightLogical(Avx2.MultiplyLow(loA, loB), PairShift);
        var hi = Avx2.ShiftRightLogical(Avx2.MultiplyLow(hiA, hiB), PairShift);

        var packed = Avx2.PackUnsignedSaturate(lo, hi);
        packed = Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsByte();
        packed.StoreUnsafe(ref dst, dstIndex);
    }

    // One int8 matrix row against the unsigned-byte activation buffer. Shared
    // by both hidden layers so they cannot drift apart, and identical in shape
    // to the arch 2 kernel: VPMADDUBSW then VPMADDWD.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DotInt8(ReadOnlySpan<byte> act, sbyte[] weights, int rowBase, int inputs,
                               int bias)
    {
        if (Avx2.IsSupported && inputs % 32 == 0)
        {
            ref byte actRef = ref MemoryMarshal.GetReference(act);
            ref sbyte wRef = ref MemoryMarshal.GetArrayDataReference(weights);
            var ones = Vector256.Create((short)1);
            var accum = Vector256<int>.Zero;
            for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<byte>.Count)
            {
                var a = Vector256.LoadUnsafe(ref actRef, i);
                var w = Vector256.LoadUnsafe(ref wRef, (nuint)rowBase + i);
                accum = Avx2.Add(accum,
                                 Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(a, w), ones));
            }
            return bias + Vector256.Sum(accum);
        }

        int sum = bias;
        for (int i = 0; i < inputs; i++)
            sum += weights[rowBase + i] * act[i];
        return sum;
    }

    // Four output rows at once, with ONE horizontal reduction instead of four.
    //
    // WHY THIS MATTERS HERE AND DID NOT BEFORE. A horizontal sum of a 256-bit
    // vector is a chain of shuffles and adds - fixed cost per output row,
    // independent of how long the row is. Architecture 3 ran 32 rows of 256
    // bytes, so the reduction was amortised over eight vector iterations.
    // Architecture 5 runs 64 rows (two layers) and its second layer's rows are
    // 64 bytes, i.e. TWO iterations, so the reduction stops being a rounding
    // error and starts being the loop.
    //
    // The trick is standard: accumulate four rows in four registers, then fold
    // them together with two rounds of horizontal add, which lands the four
    // totals in the four lanes of one 128-bit register.
    //
    //   hadd(a, b) pairs WITHIN each 128-bit lane, so after
    //     s01 = hadd(a0, a1)   s23 = hadd(a2, a3)   s = hadd(s01, s23)
    //   the low half of s holds the four rows' low-lane totals and the high
    //   half holds their high-lane totals; adding the halves finishes it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DotInt8Quad(ReadOnlySpan<byte> act, sbyte[] weights, int rowBase,
                                    int inputs, ReadOnlySpan<int> bias, Span<int> dest)
    {
        ref byte actRef = ref MemoryMarshal.GetReference(act);
        ref sbyte wRef = ref MemoryMarshal.GetArrayDataReference(weights);
        var ones = Vector256.Create((short)1);

        var acc0 = Vector256<int>.Zero;
        var acc1 = Vector256<int>.Zero;
        var acc2 = Vector256<int>.Zero;
        var acc3 = Vector256<int>.Zero;

        nuint r0 = (nuint)rowBase;
        nuint r1 = r0 + (nuint)inputs;
        nuint r2 = r1 + (nuint)inputs;
        nuint r3 = r2 + (nuint)inputs;

        for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<byte>.Count)
        {
            var a = Vector256.LoadUnsafe(ref actRef, i);
            acc0 = Avx2.Add(acc0, Avx2.MultiplyAddAdjacent(
                Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r0 + i)), ones));
            acc1 = Avx2.Add(acc1, Avx2.MultiplyAddAdjacent(
                Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r1 + i)), ones));
            acc2 = Avx2.Add(acc2, Avx2.MultiplyAddAdjacent(
                Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r2 + i)), ones));
            acc3 = Avx2.Add(acc3, Avx2.MultiplyAddAdjacent(
                Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r3 + i)), ones));
        }

        var folded = Avx2.HorizontalAdd(Avx2.HorizontalAdd(acc0, acc1),
                                        Avx2.HorizontalAdd(acc2, acc3));
        var totals = Sse2.Add(folded.GetLower(), folded.GetUpper());

        dest[0] = bias[0] + totals.GetElement(0);
        dest[1] = bias[1] + totals.GetElement(1);
        dest[2] = bias[2] + totals.GetElement(2);
        dest[3] = bias[3] + totals.GetElement(3);
    }

    // One int8 layer: rows in groups of four where the shapes allow it, one at
    // a time otherwise. Both branches compute the same sums in the same order,
    // so the fallback is a slower twin rather than a second definition.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void PropagateInt8(ReadOnlySpan<byte> act, sbyte[] weights, int weightBase,
                                      int inputs, int[] bias, int biasBase, Span<int> pre)
    {
        int rows = pre.Length;
        int o = 0;
        if (Avx2.IsSupported && inputs % 32 == 0)
        {
            for (; o + 4 <= rows; o += 4)
                DotInt8Quad(act, weights, weightBase + o * inputs, inputs,
                            bias.AsSpan(biasBase + o, 4), pre.Slice(o, 4));
        }
        for (; o < rows; o++)
            pre[o] = DotInt8(act, weights, weightBase + o * inputs, inputs, bias[biasBase + o]);
    }

    // The output layer's dot product: int16 weights against unsigned-byte
    // activations. Architecture 3's output reads 32 values and a scalar loop is
    // the right shape for that; architecture 5's reads 2*l1 + 2*l2 = 128,
    // because it sees both hidden layers and both of their activations, and at
    // that length the scalar loop is the largest remaining serial stretch of
    // the evaluation.
    //
    // NO OVERFLOW, and it is worth stating rather than assuming: the exporter
    // clips output weights to +/-127 and activations are bounded by QA = 127,
    // so the largest possible total is 128 * 127 * 127 = 2,064,512, which is
    // four hundred times inside int32. The bias is added in long afterwards
    // because it is stored as int32 and could sit anywhere in that range.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int OutputDot(ReadOnlySpan<byte> act, short[] weights, int weightBase)
    {
        int n = act.Length;
        int i = 0;
        int total = 0;

        if (Avx2.IsSupported && n >= 16)
        {
            ref byte actRef = ref MemoryMarshal.GetReference(act);
            ref short wRef = ref MemoryMarshal.GetArrayDataReference(weights);
            var accum = Vector256<int>.Zero;
            int limit = n - (n % 16);
            for (; i < limit; i += 16)
            {
                // Zero-extend 16 activations to int16; they are unsigned bytes,
                // so the widening is exact and needs no sign handling.
                var widened = Avx2.ConvertToVector256Int16(
                    Vector128.LoadUnsafe(ref actRef, (nuint)i));
                var w = Vector256.LoadUnsafe(ref wRef, (nuint)(weightBase + i));
                accum = Avx2.Add(accum, Avx2.MultiplyAddAdjacent(widened, w));
            }
            total = Vector256.Sum(accum);
        }

        for (; i < n; i++)
            total += weights[weightBase + i] * act[i];
        return total;
    }

    // The whole arch 5 forward pass. There is deliberately ONE implementation
    // rather than a scalar reference and a SIMD twin: the only vectorised parts
    // are the pairwise packer and the two dot products, and each of those
    // carries its own portable fallback inside, so the paths cannot disagree by
    // construction instead of merely by test. The layers themselves - 32 and 32
    // wide - are far too small for a vector version of the activation to be
    // worth a second copy of the arithmetic.
    private static int EvaluateArchFive(NnueNetwork net, short[] stmAccumulator,
                                        short[] oppAccumulator, int bucket)
    {
        NnueProfiling.CountEvaluation();

        int qa = net.QA;
        int qb = net.QB;
        int qbShift = net.QbShift;
        byte[] squared = net.SquaredActivation!;
        int half = net.PairOutputs;
        int l1Inputs = 2 * half;
        int l1Out = net.L1Outputs;
        int l2Out = net.L2Outputs;
        int l2Inputs = 2 * l1Out;
        int outInputs = 2 * l1Out + 2 * l2Out;

        Span<byte> act0 = stackalloc byte[l1Inputs];
        if (Avx2.IsSupported && half % 32 == 0)
            PairwiseAvx2(stmAccumulator, oppAccumulator, act0, qa, half);
        else
            PairwiseScalar(stmAccumulator, oppAccumulator, act0, qa, half);

        // ---- first hidden layer ----
        sbyte[] w1 = net.L1WeightsI8!;
        int w1Base = bucket * l1Out * l1Inputs;
        int b1Base = bucket * l1Out;
        Span<int> pre1 = stackalloc int[l1Out];
        PropagateInt8(act0, w1, w1Base, l1Inputs, net.L1Bias, b1Base, pre1);

        Span<byte> act1 = stackalloc byte[l2Inputs];
        DualActivate(pre1, act1, qa, qb, qbShift, squared);

        // ---- second hidden layer ----
        sbyte[] w2 = net.L2Weights!;
        int w2Base = bucket * l2Out * l2Inputs;
        int b2Base = bucket * l2Out;
        Span<int> pre2 = stackalloc int[l2Out];
        PropagateInt8(act1, w2, w2Base, l2Inputs, net.L2Bias!, b2Base, pre2);

        Span<byte> act2 = stackalloc byte[2 * l2Out];
        DualActivate(pre2, act2, qa, qb, qbShift, squared);

        // ---- output over BOTH layers' activations ----
        int outBase = bucket * outInputs;
        short[] outW = net.OutWeights;
        long output = net.OutBias[bucket]
                    + OutputDot(act1, outW, outBase)
                    + OutputDot(act2, outW, outBase + l2Inputs);

        // The linear bypass. pre1 is in QA*QB units, which is exactly the unit
        // of the output accumulator, so these two neurons add a score that
        // never passes through a clamp - the network's way of carrying a large
        // material difference without saturating. No rescaling is involved:
        // the units already agree, which is the rare case in a ported term.
        output += pre1[l1Out - 2] - pre1[l1Out - 1];

        return (int)(output * net.OutputScale / ((long)qa * qb));
    }

    // ---- Shared tail: hidden activations -> output -> centipawns ----
    // Identical for every architecture; only the way 'hidden' was produced
    // differs. Kept in one place so the paths cannot drift apart.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FinishOutput(NnueNetwork net, ReadOnlySpan<int> hidden, int bucket)
    {
        int qa = net.QA;
        int headOffset = bucket * net.L1Outputs;
        long output = net.OutBias[bucket];

        // QB is a power of two (64) in every shipped net, but the JIT only sees
        // a field, so `hidden[o] / net.QB` compiles to a real divide - one per
        // hidden unit, thirty-two per evaluation. The shift is EXACT here: it
        // differs from the division only for negative values, where it floors
        // instead of truncating, and the clamp immediately maps both to zero.
        //
        // Worth doing rather than a micro-optimisation: the same divisions cost
        // architecture 5 thirteen percent of its NPS when it had four times as
        // many of them, measured, which is how this one was found.
        int shift = net.QbShift;
        if (shift >= 0)
        {
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int a2 = Math.Clamp(hidden[o] >> shift, 0, qa);
                output += net.OutWeights[headOffset + o] * (long)a2;
            }
        }
        else
        {
            for (int o = 0; o < net.L1Outputs; o++)
            {
                int a2 = Math.Clamp(hidden[o] / net.QB, 0, qa);
                output += net.OutWeights[headOffset + o] * (long)a2;
            }
        }
        return (int)(output * net.OutputScale / ((long)qa * net.QB));
    }

    public static int EvaluateScalar(NnueNetwork net, short[] stmAccumulator, short[] oppAccumulator,
                                     int bucket = 0)
    {
        if (net.UsesDualActivation)
            return EvaluateArchFive(net, stmAccumulator, oppAccumulator, bucket);

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
        if (net.UsesDualActivation)
            return EvaluateArchFive(net, stmAccumulator, oppAccumulator, bucket);

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
    // bytes - the weight stream is the part that does not fit in cache.
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

            // FOUR OUTPUT ROWS PER PASS, one horizontal reduction instead of
            // four. This is the shipping net's kernel - fq60 is architecture 1,
            // QA=255 - and the search profile puts this method at 23.8% of ALL
            // search time, the second largest item after Negamax itself. It was
            // costing one Vector256.Sum per output row, thirty-two per
            // evaluation, each a chain of shuffles and adds whose cost does not
            // depend on how long the row is.
            //
            // Reading the activation vector once for four rows instead of once
            // per row also cuts three quarters of those loads, and the four
            // accumulator chains are independent, so they issue in parallel
            // where one chain had to serialise.
            //
            // The fold is the same identity the arch 5 kernel uses: hadd pairs
            // WITHIN each 128-bit lane, so after two rounds the low half holds
            // the four rows' low-lane totals and the high half their high-lane
            // totals, and adding the halves finishes it.
            int o = 0;
            for (; o + 4 <= net.L1Outputs; o += 4)
            {
                nuint r0 = (nuint)(weightBase + o * inputs);
                nuint r1 = r0 + (nuint)inputs;
                nuint r2 = r1 + (nuint)inputs;
                nuint r3 = r2 + (nuint)inputs;

                var a0 = Vector256<int>.Zero;
                var a1 = Vector256<int>.Zero;
                var a2 = Vector256<int>.Zero;
                var a3 = Vector256<int>.Zero;

                for (nuint i = 0; i < (nuint)inputs; i += (nuint)Vector256<short>.Count)
                {
                    var a = Vector256.LoadUnsafe(ref actRef, i);
                    a0 = Avx2.Add(a0, Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r0 + i)));
                    a1 = Avx2.Add(a1, Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r1 + i)));
                    a2 = Avx2.Add(a2, Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r2 + i)));
                    a3 = Avx2.Add(a3, Avx2.MultiplyAddAdjacent(a, Vector256.LoadUnsafe(ref wRef, r3 + i)));
                }

                var folded = Avx2.HorizontalAdd(Avx2.HorizontalAdd(a0, a1),
                                                Avx2.HorizontalAdd(a2, a3));
                var totals = Sse2.Add(folded.GetLower(), folded.GetUpper());
                hidden[o] = net.L1Bias[biasBase + o] + totals.GetElement(0);
                hidden[o + 1] = net.L1Bias[biasBase + o + 1] + totals.GetElement(1);
                hidden[o + 2] = net.L1Bias[biasBase + o + 2] + totals.GetElement(2);
                hidden[o + 3] = net.L1Bias[biasBase + o + 3] + totals.GetElement(3);
            }

            // Tail, for widths that are not a multiple of four. Same sums in
            // the same order, so it is a slower twin and not a second
            // definition of the arithmetic.
            for (; o < net.L1Outputs; o++)
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
