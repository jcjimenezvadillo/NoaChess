namespace NoaChess.Engine.Evaluation.Nnue;

// The loaded network weights, ready for inference. Everything is stored in
// the quantized integer form the inference code consumes directly — there is
// no float math at runtime.
//
// Quantization contract (mirrored by tools/training/nnue/export_model.py):
//   ftWeights  = round(w_float * QA)          -> int16
//   ftBias     = round(b_float * QA)          -> int16
//   l1Weights  = round(w_float * QB)          -> int16
//   l1Bias     = round(b_float * QA * QB)     -> int32
//   outWeights = round(w_float * QB)          -> int16
//   outBias    = round(b_float * QA * QB)     -> int32
//
// Inference (see NnueInference):
//   accumulator = ftBias + sum(ftWeights[activeFeature])       (int16)
//   a  = clamp(accumulator, 0, QA)                             per perspective
//   h  = l1Bias[o] + dot(l1Weights[o], concat(aStm, aOpp))     (int32)
//   a2 = clamp(h / QB, 0, QA)
//   out = outBias + dot(outWeights, a2)                        (int64)
//   centipawns = out * OutputScale / (QA * QB)
public sealed class NnueNetwork
{
    public required int FtInputs { get; init; }
    public required int FtOutputs { get; init; }
    public required int L1Outputs { get; init; }
    public required int QA { get; init; }
    public required int QB { get; init; }
    public required int OutputScale { get; init; }

    public required short[] FtWeights { get; init; }   // [FtInputs * FtOutputs]
    public required short[] FtBias { get; init; }      // [FtOutputs]
    public required short[] L1Weights { get; init; }   // [L1Outputs * 2*FtOutputs]
    public required int[] L1Bias { get; init; }        // [L1Outputs]
    public required short[] OutWeights { get; init; }  // [L1Outputs]
    public required int OutBias { get; init; }

    // Identifies the loaded model (payload hash) for logging/reproducibility.
    public required string Sha256 { get; init; }
}
