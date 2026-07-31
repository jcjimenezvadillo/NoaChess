namespace NoaChess.Engine.Evaluation.Nnue;

// The loaded network weights, ready for inference. Everything is stored in
// the quantized integer form the inference code consumes directly — there is
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
// Inference (see NnueInference) — IDENTICAL FORMULAS FOR BOTH ARCHITECTURES,
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
    public required int QA { get; init; }
    public required int QB { get; init; }
    public required int OutputScale { get; init; }

    public required short[] FtWeights { get; init; }   // [FtInputs * FtOutputs]
    public required short[] FtBias { get; init; }      // [FtOutputs]
    // Exactly one of the two L1 weight arrays is populated, selected by
    // ArchitectureId. Keeping them separate (rather than widening int8 to
    // int16 at load) is the whole point: the int8 array is half the bytes, and
    // the L1 dot product is a streaming read over it.
    public short[]? L1Weights { get; init; }           // arch 1: [L1Outputs * 2*FtOutputs]
    public sbyte[]? L1WeightsI8 { get; init; }          // arch 2: [L1Outputs * 2*FtOutputs]
    public required int[] L1Bias { get; init; }        // [L1Outputs]
    public required short[] OutWeights { get; init; }  // [L1Outputs]
    public required int OutBias { get; init; }

    public bool UsesInt8L1 => ArchitectureId == NnueModelHeader.ArchitectureInt8L1;

    // Identifies the loaded model (payload hash) for logging/reproducibility.
    public required string Sha256 { get; init; }
}
