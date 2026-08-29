using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// Pins architecture 5's forward pass against a plain, obviously-correct
// reference written here in the test.
//
// WHY A SECOND IMPLEMENTATION AND NOT JUST "it runs". The production path packs
// the pairwise products with PackUnsignedSaturate plus a Permute4x64, and sums
// four output rows at a time by folding four accumulators with two horizontal
// adds. Both of those are lane-order tricks: get the permute control or the
// hadd pairing wrong and every activation is multiplied by the wrong weight,
// which does not throw, does not look wrong, and trains into a net that simply
// plays a little worse forever. The reference below does the same arithmetic
// with nested loops and no intrinsics, so a lane-order mistake shows up as a
// number that differs instead of as a season of bad results.
//
// The C#-to-Python half of the contract is checked separately, by
// tools/training/nnue/verify_export.py against a real exported file.
public class NnueArchFiveTests
{
    private const int FtInputs = 64;   // small, the transformer is not what is under test
    private const int FtOutputs = 64;  // must be even to pair, and a multiple of 64 to vectorise
    private const int L1Outputs = 32;
    private const int L2Outputs = 32;
    private const int Qa = 127;
    private const int Qb = 64;

    private static NnueNetwork BuildNetwork(int buckets, int seed)
    {
        var random = new Random(seed);
        int l1Inputs = FtOutputs;              // pairwise halves 2*FtOutputs
        int l2Inputs = 2 * L1Outputs;
        int outInputs = 2 * L1Outputs + 2 * L2Outputs;

        sbyte[] NextInt8(int count)
        {
            var result = new sbyte[count];
            for (int i = 0; i < count; i++)
                result[i] = (sbyte)random.Next(-127, 128);
            return result;
        }
        int[] NextBias(int count)
        {
            var result = new int[count];
            for (int i = 0; i < count; i++)
                result[i] = random.Next(-40_000, 40_000);
            return result;
        }
        short[] NextInt16(int count, int limit)
        {
            var result = new short[count];
            for (int i = 0; i < count; i++)
                result[i] = (short)random.Next(-limit, limit + 1);
            return result;
        }

        var squared = new byte[Qa + 1];
        for (int c = 0; c <= Qa; c++)
            squared[c] = (byte)(c * c / Qa);

        return new NnueNetwork
        {
            ArchitectureId = NnueModelHeader.ArchitectureDualActivation,
            FtInputs = FtInputs,
            FtOutputs = FtOutputs,
            L1Outputs = L1Outputs,
            L2Outputs = L2Outputs,
            OutputBuckets = buckets,
            QA = Qa,
            QB = Qb,
            QbShift = 6,
            SquaredActivation = squared,
            OutputScale = 400,
            FtWeights = NextInt16(FtInputs * FtOutputs, 64),
            FtBias = NextInt16(FtOutputs, 64),
            L1WeightsI8 = NextInt8(buckets * L1Outputs * l1Inputs),
            L1Bias = NextBias(buckets * L1Outputs),
            L2Weights = NextInt8(buckets * L2Outputs * l2Inputs),
            L2Bias = NextBias(buckets * L2Outputs),
            OutWeights = NextInt16(buckets * outInputs, 127),
            OutBias = NextBias(buckets),
            Sha256 = "test"
        };
    }

    // Loops and nothing else. Deliberately written from the architecture's
    // description rather than by copying the production code, so that a bug
    // present in both would have to be made twice.
    private static int Reference(NnueNetwork net, short[] stm, short[] opp, int bucket)
    {
        int half = net.FtOutputs / 2;
        int l1Inputs = 2 * half;
        int l2Inputs = 2 * net.L1Outputs;

        var act0 = new int[l1Inputs];
        for (int j = 0; j < half; j++)
        {
            int a0 = Math.Clamp((int)stm[j], 0, Qa);
            int a1 = Math.Clamp((int)stm[j + half], 0, Qa);
            act0[j] = (a0 * a1) >> NnueInference.PairShift;

            int b0 = Math.Clamp((int)opp[j], 0, Qa);
            int b1 = Math.Clamp((int)opp[j + half], 0, Qa);
            act0[half + j] = (b0 * b1) >> NnueInference.PairShift;
        }

        var pre1 = new int[net.L1Outputs];
        for (int o = 0; o < net.L1Outputs; o++)
        {
            int sum = net.L1Bias[bucket * net.L1Outputs + o];
            int row = bucket * net.L1Outputs * l1Inputs + o * l1Inputs;
            for (int i = 0; i < l1Inputs; i++)
                sum += net.L1WeightsI8![row + i] * act0[i];
            pre1[o] = sum;
        }

        int[] Activate(int[] pre)
        {
            var act = new int[2 * pre.Length];
            for (int o = 0; o < pre.Length; o++)
            {
                int c = Math.Clamp(pre[o] / Qb, 0, Qa);
                act[o] = c * c / Qa;
                act[pre.Length + o] = c;
            }
            return act;
        }

        int[] act1 = Activate(pre1);

        var pre2 = new int[net.L2Outputs];
        for (int o = 0; o < net.L2Outputs; o++)
        {
            int sum = net.L2Bias![bucket * net.L2Outputs + o];
            int row = bucket * net.L2Outputs * l2Inputs + o * l2Inputs;
            for (int i = 0; i < l2Inputs; i++)
                sum += net.L2Weights![row + i] * act1[i];
            pre2[o] = sum;
        }

        int[] act2 = Activate(pre2);

        int outInputs = 2 * net.L1Outputs + 2 * net.L2Outputs;
        long output = net.OutBias[bucket];
        for (int i = 0; i < act1.Length; i++)
            output += net.OutWeights[bucket * outInputs + i] * (long)act1[i];
        for (int i = 0; i < act2.Length; i++)
            output += net.OutWeights[bucket * outInputs + act1.Length + i] * (long)act2[i];

        output += pre1[net.L1Outputs - 2] - pre1[net.L1Outputs - 1];
        return (int)(output * net.OutputScale / ((long)Qa * Qb));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void ForwardPassMatchesThePlainReference(int buckets)
    {
        NnueNetwork net = BuildNetwork(buckets, seed: 20260821);
        var random = new Random(7);

        for (int trial = 0; trial < 200; trial++)
        {
            var stm = new short[FtOutputs];
            var opp = new short[FtOutputs];
            for (int i = 0; i < FtOutputs; i++)
            {
                // The range straddles the clamp on both sides on purpose: the
                // pairwise product's bound only holds because both operands are
                // clipped first, so negatives and values above QA have to be in
                // the sample rather than assumed away.
                stm[i] = (short)random.Next(-400, 400);
                opp[i] = (short)random.Next(-400, 400);
            }

            int bucket = random.Next(buckets);
            Assert.Equal(Reference(net, stm, opp, bucket),
                         NnueInference.Evaluate(net, stm, opp, bucket));
        }
    }

    // The pairwise product feeds an int8 dot product whose int16 lane must not
    // saturate. That bound is what makes the kernel exact for EVERY position
    // rather than for the ones the tests happen to draw, so it is asserted at
    // its extreme rather than sampled.
    [Fact]
    public void PairwiseProductStaysInsideTheUnsignedByte()
    {
        int worstPair = (Qa * Qa) >> NnueInference.PairShift;
        Assert.Equal(126, worstPair);
        Assert.True(worstPair <= byte.MaxValue);

        // VPMADDUBSW computes a0*w0 + a1*w1 into one int16 lane. The second
        // hidden layer's activations reach QA, not just 126, so QA is the bound
        // that has to hold.
        Assert.True(2 * Qa * 127 <= short.MaxValue);
    }
}
