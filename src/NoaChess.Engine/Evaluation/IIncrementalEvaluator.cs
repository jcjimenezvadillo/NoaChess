using NoaChess.Core;

namespace NoaChess.Engine.Evaluation;

// Optional contract for evaluators that maintain incremental state in sync
// with the search's make/unmake stream (NNUE accumulators). The search calls
// these hooks; evaluators that do not need them (classical) simply do not
// implement the interface, at zero cost.
//
// Discipline: PushMove is called with the board still in the PRE-move
// position; Pop is called after the corresponding UnmakeMove. Push and Pop
// must pair up exactly - the tests verify incremental results stay identical
// to full recomputation across random make/unmake sequences.
public interface IIncrementalEvaluator : IPositionEvaluator
{
    // Re-anchors the incremental state at a new search root.
    void Reset(Board board);

    // A move is about to be made on 'board'.
    void PushMove(Board board, Move move);

    // The move pushed above HAS been made, and 'board' is now in the position
    // after it. Only an evaluator whose features depend on the whole position
    // needs this; the rest can ignore it.
    //
    // It exists because threat features cannot be replayed from a recorded
    // (piece, from, to) the way HalfKA can: they depend on what the moved piece
    // now attacks, on what attacks it, and on every discovered relation where a
    // slider's ray opened or closed. Deriving that needs the position on BOTH
    // sides of the move, and the pre-move one is gone the instant it is made.
    void CompleteThreatDelta(Board board);

    // A null move (pass) is about to be made.
    void PushNull();

    // The last pushed move/null was unmade.
    void Pop();
}
