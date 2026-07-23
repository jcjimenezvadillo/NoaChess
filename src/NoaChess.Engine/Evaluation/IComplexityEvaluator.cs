namespace NoaChess.Engine.Evaluation;

// Implemented by evaluators that can report how COMPLEX the last evaluated
// position was (centipawns, never negative — the classical evaluator exposes
// the winnable initiative magnitude). The reference-style NMP entry gate that
// consumed it was measured and cut in v2.7.1, so the current search deliberately
// leaves this as future plumbing for time management or an NNUE-era NMP revisit.
public interface IComplexityEvaluator
{
    // Complexity of the position given to the most recent Evaluate() call.
    int LastComplexity { get; }
}
