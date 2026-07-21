namespace NoaChess.Engine.Evaluation.Nnue;

// NNUE-related configuration shared by the UCI host and the engine facade.
// Immutable per search, per the performance rules of the technical roadmap.
public sealed class NnueOptions
{
    // Whether the NNUE evaluator should be used instead of the classical one
    // (only honored when a valid model is loaded).
    public bool UseNnue { get; set; }

    // Path to the .noannue model file ("EvalFile" UCI option).
    public string EvalFile { get; set; } = "";
}
