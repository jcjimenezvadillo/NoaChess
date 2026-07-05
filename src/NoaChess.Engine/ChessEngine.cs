using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;

namespace NoaChess.Engine;

// Engine facade: the single entry point used by the GUI and the UCI host.
// It encapsulates the internal wiring (evaluator + search) so consumers do not
// depend on the engine's internal classes.
public sealed class ChessEngine
{
    private readonly AlphaBetaSearch _search = new(new ClassicalEvaluator());

    // Default search depth for the MVP.
    public int DefaultDepth { get; set; } = 4;

    // Searches for the best move for the current board position. It is a
    // SYNCHRONOUS and potentially slow call: interactive consumers (GUI) must
    // invoke it from a background thread and use the token to be able to cancel it.
    // 'progress' (optional) receives a snapshot after each completed search depth.
    public SearchResult FindBestMove(Board board, int? depth = null,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
        => _search.FindBestMove(board, depth ?? DefaultDepth, cancellation, progress);
}
