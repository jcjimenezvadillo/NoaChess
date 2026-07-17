namespace NoaChess.Engine.Evaluation;

// Implemented by evaluators that can report how COMPLEX the last evaluated
// position was (centipawns, never negative — the classical evaluator exposes
// the winnable initiative magnitude). The search's null-move-pruning entry
// margin consumes it: a complex position gets a higher bar before the side to
// move is allowed to "pass", because passing costs more where there is real
// play left. Evaluators without the notion simply don't implement this and
// the search falls back to zero (the term vanishes).
public interface IComplexityEvaluator
{
    // Complexity of the position given to the most recent Evaluate() call.
    int LastComplexity { get; }
}
