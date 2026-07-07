using NoaChess.Core;

namespace NoaChess.Engine.Evaluation;

// Abstraction of the position evaluator. The search depends on this interface
// and not on a concrete implementation, so that in the future the classical
// evaluation can be swapped for NNUE (or combined with it) without touching
// the search code.
public interface IPositionEvaluator
{
    // Score in centipawns (100 = one pawn of advantage) FROM THE POINT OF VIEW
    // OF THE SIDE TO MOVE. This convention ("side to move relative") is what
    // negamax needs: a positive value always means "good for the mover",
    // whether it is white or black.
    int Evaluate(Board board);
}
