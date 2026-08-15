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
    public required short[] OutWeights { get; init; }  // [Buckets * L1Outputs]
    public required int[] OutBias { get; init; }       // [Buckets]

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
                           || ArchitectureId == NnueModelHeader.ArchitectureThreats;

    public bool UsesOutputBuckets => OutputBuckets > 1;

    // True when this net evaluates threat features as well as HalfKA. Read off
    // the weights rather than the architecture id, so a net that claims arch 4
    // without carrying them can never be silently treated as if it did.
    public bool UsesThreats => ThreatWeights is not null;

    // Identifies the loaded model (payload hash) for logging/reproducibility.
    public required string Sha256 { get; init; }
}
