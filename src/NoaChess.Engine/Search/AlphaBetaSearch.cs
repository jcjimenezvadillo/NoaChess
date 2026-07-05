using NoaChess.Core;
using NoaChess.Engine.Evaluation;

namespace NoaChess.Engine.Search;

// Result of a search: best move and its score.
public readonly record struct SearchResult(Move BestMove, int Score, long NodesSearched);

// Progress snapshot reported after each completed iterative-deepening
// iteration. Consumers (GUI status bar, UCI "info" lines) use it to show the
// evaluation and the depth being analyzed while the engine thinks.
public readonly record struct SearchProgress(int Depth, int Score, long NodesSearched, Move BestMove);

// Basic Alpha-Beta search (negamax formulation) driven by iterative deepening.
//
// Negamax is a simplification of minimax: since chess is zero-sum,
// "what is good for me is bad for you", so instead of alternating max/min
// functions the score is negated when switching sides: score = -Search(opponent).
//
// Alpha-Beta is the pruning that makes the search viable: alpha is "the best I
// already have guaranteed" and beta "the best the opponent will allow me". If
// a branch returns >= beta, the opponent will never enter it, and it is pruned
// without exploring it fully.
//
// Iterative deepening searches depth 1, then 2, then 3... up to the target.
// It looks wasteful but the shallow iterations are a tiny fraction of the
// total cost, and in exchange we get progress reporting after each depth and
// a complete result to fall back on if the search is cancelled mid-iteration.
//
// It deliberately does NOT include yet: quiescence, transposition table or
// time management (they arrive in v0.2 per the roadmap).
public sealed class AlphaBetaSearch(IPositionEvaluator evaluator)
{
    // "Infinite" and mate scores. Mate is encoded far from int.MaxValue so the
    // ply can be added/subtracted without overflowing. MateScore is public so
    // UI layers can recognize mate scores and display "mate in N" instead of
    // a huge centipawn number.
    private const int Infinity = 1_000_000;
    public const int MateScore = 100_000;

    private readonly IPositionEvaluator _evaluator = evaluator;
    private long _nodes;
    private CancellationToken _cancellation;

    // Searches for the best move using iterative deepening up to 'depth'.
    // The CancellationToken lets the GUI abort the search without blocking the
    // interface; in that case the result of the last completed iteration is
    // returned. 'progress' (optional) is invoked after each completed depth.
    public SearchResult FindBestMove(Board board, int depth,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
    {
        if (depth < 1)
            throw new ArgumentOutOfRangeException(nameof(depth), "Minimum depth is 1.");

        _nodes = 0;
        _cancellation = cancellation;

        SearchResult best = default;

        for (int d = 1; d <= depth; d++)
        {
            SearchResult iteration = SearchAtDepth(board, d);

            // A cancelled iteration may be based on partial information: keep
            // the previous (complete) result unless we have nothing at all.
            if (cancellation.IsCancellationRequested)
            {
                if (best.BestMove == Move.None)
                    best = iteration;
                break;
            }

            best = iteration;
            progress?.Report(new SearchProgress(d, iteration.Score, _nodes, iteration.BestMove));
        }

        return best;
    }

    // One fixed-depth Alpha-Beta search from the root.
    private SearchResult SearchAtDepth(Board board, int depth)
    {
        var moves = MoveGenerator.GenerateLegalMoves(board);
        OrderMoves(moves);

        Move bestMove = Move.None;
        int bestScore = -Infinity;
        int alpha = -Infinity;

        // The root is handled separately so we can remember WHICH move produced
        // the best score (inner nodes only return the score).
        foreach (Move move in moves)
        {
            if (_cancellation.IsCancellationRequested && bestMove != Move.None)
                break; // Cancelled: return the best seen so far.

            board.MakeMove(move);
            int score = -Negamax(board, depth - 1, -Infinity, -alpha, ply: 1);
            board.UnmakeMove();

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
                alpha = Math.Max(alpha, score);
            }
        }

        return new SearchResult(bestMove, bestScore, _nodes);
    }

    private int Negamax(Board board, int depth, int alpha, int beta, int ply)
    {
        _nodes++;

        // Cancellation is checked every so many nodes to avoid paying the token
        // cost at every node. Returning alpha cuts the branch cleanly.
        if ((_nodes & 0xFFF) == 0 && _cancellation.IsCancellationRequested)
            return alpha;

        if (board.HalfmoveClock >= 100)
            return 0; // Draw by the fifty-move rule.

        if (depth == 0)
            return _evaluator.Evaluate(board);

        var moves = MoveGenerator.GenerateLegalMoves(board);

        // No legal moves: mate or stalemate. The ply is added to the mate score
        // so the engine prefers the SHORTEST mate (mate in 2 scores better than
        // in 5) and drags it out as long as possible when it is the one being mated.
        if (moves.Count == 0)
            return board.IsInCheck() ? -MateScore + ply : 0;

        OrderMoves(moves);

        foreach (Move move in moves)
        {
            board.MakeMove(move);
            int score = -Negamax(board, depth - 1, -beta, -alpha, ply + 1);
            board.UnmakeMove();

            if (score >= beta)
                return beta; // Beta cutoff: the opponent will never allow reaching this.
            if (score > alpha)
                alpha = score;
        }

        return alpha;
    }

    // Very simple move ordering: captures first. A good ordering makes
    // Alpha-Beta prune much earlier (ideally the best move is tried first).
    // In v0.2 it will be replaced by MVV-LVA, killer moves and history.
    private static void OrderMoves(List<Move> moves) =>
        moves.Sort(static (a, b) => b.IsCapture.CompareTo(a.IsCapture));
}
