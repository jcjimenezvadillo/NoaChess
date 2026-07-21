using NoaChess.Core;

namespace NoaChess.Engine.Evaluation;

// Optional contract for evaluators that maintain incremental state in sync
// with the search's make/unmake stream (NNUE accumulators). The search calls
// these hooks; evaluators that do not need them (classical) simply do not
// implement the interface, at zero cost.
//
// Discipline: PushMove is called with the board still in the PRE-move
// position; Pop is called after the corresponding UnmakeMove. Push and Pop
// must pair up exactly — the tests verify incremental results stay identical
// to full recomputation across random make/unmake sequences.
public interface IIncrementalEvaluator : IPositionEvaluator
{
    // Re-anchors the incremental state at a new search root.
    void Reset(Board board);

    // A move is about to be made on 'board'.
    void PushMove(Board board, Move move);

    // A null move (pass) is about to be made.
    void PushNull();

    // The last pushed move/null was unmade.
    void Pop();
}
