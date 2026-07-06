using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;

namespace NoaChess.Engine;

// Engine facade: the single entry point used by the GUI and the UCI host.
// It encapsulates the internal wiring (evaluator + search) so consumers do not
// depend on the engine's internal classes.
//
// NOTE: the engine keeps state between searches (transposition table, history
// heuristic), which is a big part of its strength — but it also means a single
// instance must not run two searches CONCURRENTLY. Callers are responsible for
// finishing/cancelling one search before starting the next.
public sealed class ChessEngine
{
    private readonly AlphaBetaSearch _search = new(new ClassicalEvaluator());

    // Default search depth when no explicit limit is given. v0.2's TT,
    // quiescence, move ordering and LMR make depth 6 respond in well under a
    // second in typical middlegames (v0.1 could only afford 4).
    public int DefaultDepth { get; set; } = 6;

    // Searches with an explicit depth/time limit. Synchronous and potentially
    // slow: interactive consumers (GUI) must invoke it from a background
    // thread and use the token to be able to cancel it. 'progress' (optional)
    // receives a snapshot after each completed search depth.
    public SearchResult FindBestMove(Board board, SearchLimits limits,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
        => _search.FindBestMove(board, limits, cancellation, progress);

    // Convenience overload: fixed-depth search (DefaultDepth when omitted).
    public SearchResult FindBestMove(Board board, int? depth = null,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
        => FindBestMove(board, SearchLimits.Depth(depth ?? DefaultDepth), cancellation, progress);

    // Forgets everything learned in the current game (transposition table,
    // heuristics). Call it when a NEW game starts ("ucinewgame").
    public void NewGame() => _search.Reset();

    // Reallocates the transposition table ("setoption name Hash value N").
    public void ResizeHash(int sizeMb) => _search.ResizeTT(sizeMb);
}
