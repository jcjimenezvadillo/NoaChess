namespace NoaChess.Engine.Evaluation.Nnue;

// The loaded network weights, ready for inference. Everything is stored in
// the quantized integer form the inference code consumes directly - there is
// no float math at runtime.
//
// Quantization contract (mirrored by tools/training/nnue/export_model.py):
//   ftWeights  = round(w_float * QA)          -> int16
//   ftBias     = round(b_float * QA)          -> int16
//   l1Weights  = round(w_float * QB)          -> int16 (arch 1) / int8 (arch 2)
//   l1Bias     = round(b_float * QA * QB)     -> int32
//   outWeights = round(w_float * QB)          -> int16
//   outBias    = round(b_float * QA * QB)     -> int32
//
// Inference (see NnueInference) - IDENTICAL FORMULAS FOR BOTH ARCHITECTURES,
// only the storage width of l1Weights and the value of QA differ:
//   accumulator = ftBias + sum(ftWeights[activeFeature])       (int16)
//   a  = clamp(accumulator, 0, QA)                             per perspective
//   h  = l1Bias[o] + dot(l1Weights[o], concat(aStm, aOpp))     (int32)
//   a2 = clamp(h / QB, 0, QA)
//   out = outBias + dot(outWeights, a2)                        (int64)
//   centipawns = out * OutputScale / (QA * QB)
public sealed class NnueNetwork
{
    public required uint ArchitectureId { get; init; }
    public required int FtInputs { get; init; }
    public required int FtOutputs { get; init; }
    public required int L1Outputs { get; init; }
    // Second hidden layer width. 0 for every architecture before 5, which is
    // what makes "does this net have a second layer" a property of the file
    // rather than a second thing to keep in sync with the architecture id.
    public int L2Outputs { get; init; }
    // Head replicas selected by piece count (arch 3). 1 for arch 1/2, which
    // makes every bucket offset below collapse to zero and the older paths
    // byte-identical to what they were before buckets existed.
    public int OutputBuckets { get; init; } = 1;
    public required int QA { get; init; }
    public required int QB { get; init; }
    public required int OutputScale { get; init; }

    public required short[] FtWeights { get; init; }   // [FtInputs * FtOutputs]
    public required short[] FtBias { get; init; }      // [FtOutputs]
    // Exactly one of the two L1 weight arrays is populated, selected by
    // ArchitectureId. Keeping them separate (rather than widening int8 to
    // int16 at load) is the whole point: the int8 array is half the bytes, and
    // the L1 dot product is a streaming read over it.
    // All head arrays are BUCKET-MAJOR: bucket b occupies the slice starting at
    // b * <per-bucket size>. With OutputBuckets == 1 that slice is the whole
    // array and the indexing is exactly the pre-v4.2.0 indexing.
    public short[]? L1Weights { get; init; }           // arch 1: [Buckets * L1Outputs * 2*FtOutputs]
    public sbyte[]? L1WeightsI8 { get; init; }          // arch 2/3: same shape, int8
    public required int[] L1Bias { get; init; }        // [Buckets * L1Outputs]
    // ARCH 1-4: [Buckets * L1Outputs].
    // ARCH 5:   [Buckets * (2*L1Outputs + 2*L2Outputs)], because the output
    //           reads both hidden layers and both of their activations.
    public required short[] OutWeights { get; init; }
    public required int[] OutBias { get; init; }       // [Buckets]

    // ---- Architecture 5 only, null/zero elsewhere ----
    // Second hidden layer, bucket-major like every other head array.
    public sbyte[]? L2Weights { get; init; }           // [Buckets * L2Outputs * 2*L1Outputs]
    public int[]? L2Bias { get; init; }                // [Buckets * L2Outputs]

    // The threat transformer, arch 4 only, null everywhere else.
    // [ThreatFeatureIndex.InputSize * FtOutputs], same quantisation as FtWeights
    // because both sum into the same accumulator: a row is round(w_float * QA).
    //
    // There is no threat bias. Two transformers feeding one accumulator would
    // have two constants added to the same sum, so the trainer folds the threat
    // one into FtBias at export and the engine never sees it.
    public short[]? ThreatWeights { get; init; }

    // Arch 4 belongs here, and leaving it out cost a NullReferenceException in
    // the first end-to-end run: the file stores int8 L1 weights, so L1Weights is
    // null and L1WeightsI8 holds them, and a predicate that said "not int8" sent
    // the inference straight into the null array. The loader had already been
    // taught that arch 4 is an int8 architecture; this predicate had not, and
    // the two disagreed silently until something dereferenced the difference.
    public bool UsesInt8L1 => ArchitectureId == NnueModelHeader.ArchitectureInt8L1
                           || ArchitectureId == NnueModelHeader.ArchitectureInt8L1Buckets
                           || ArchitectureId == NnueModelHeader.ArchitectureThreats
                           || ArchitectureId == NnueModelHeader.ArchitectureDualActivation;

    public bool UsesOutputBuckets => OutputBuckets > 1;

    // Architecture 5: pairwise feature-transformer read, squared activations,
    // two hidden layers and a linear bypass. Read off the second layer rather
    // than the id for the same reason UsesThreats is read off the weights - a
    // file that claims the architecture without carrying it can never be
    // silently treated as if it did.
    public bool UsesDualActivation => L2Weights is not null && L2Outputs > 0;

    // Width of the pairwise feature-transformer output, per perspective.
    public int PairOutputs => FtOutputs / 2;

    // ---- DIVISION-FREE ACTIVATIONS ----
    //
    // Both of the activation's steps are integer DIVISIONS by a value the JIT
    // only learns at runtime, so it emits a real divide instruction for each:
    //     c = clamp(pre / QB, 0, QA)     s = c * c / QA
    // Architecture 5 runs that twice per hidden unit over two layers, which at
    // 32 and 32 wide is 128 divides per evaluation against architecture 3's 32.
    // MEASURED, and it is not a micro-optimisation: the first arch 5 build ran
    // at 0.74x the NPS of a matched arch 3 net despite doing FEWER
    // multiply-accumulates, and this is where the difference went.
    //
    // Both divisions have an exact constant-time replacement:
    //   QB is a power of two (64), so pre / QB is an arithmetic shift. The two
    //   differ for negative values - truncation towards zero versus floor - but
    //   every negative result is clamped to 0 immediately after, so the clamped
    //   answers are identical for every input.
    //   c is bounded by QA, so c * c / QA has at most QA + 1 possible answers
    //   and fits in a 128-byte table that stays in L1 cache forever.
    //
    // Both are exact rather than approximate, which matters: an approximate
    // reciprocal here would change the evaluation, and the C#-to-Python parity
    // test would then be comparing two different networks.
    public int QbShift { get; init; } = -1;      // -1 when QB is not a power of two
    public byte[]? SquaredActivation { get; init; }  // [QA + 1], index by clipped value

    // True when this net evaluates threat features as well as HalfKA. Read off
    // the weights rather than the architecture id, so a net that claims arch 4
    // without carrying them can never be silently treated as if it did.
    public bool UsesThreats => ThreatWeights is not null;

    // Identifies the loaded model (payload hash) for logging/reproducibility.
    public required string Sha256 { get; init; }
}
