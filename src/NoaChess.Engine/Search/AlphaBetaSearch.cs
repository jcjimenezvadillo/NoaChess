using System.Diagnostics;
using NoaChess.Core;
using NoaChess.Engine.Evaluation;
using NoaChess.Engine.Heuristics;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Search;

// Alpha-Beta search (negamax formulation), v0.2 feature set:
//
// - Iterative deepening: search depth 1, then 2, 3... Shallow iterations are a
//   tiny fraction of the total cost and in exchange we get progress reporting,
//   a complete result to fall back on when time runs out, and previous-depth
//   information (TT move, score) that makes the next iteration much cheaper.
// - Aspiration windows: instead of an infinite (alpha, beta) window, each
//   iteration starts with a narrow window around the previous score. Narrow
//   windows cut off much more; if the true score falls outside ("fail"), the
//   iteration is re-searched with the full window.
// - Transposition table: caches search results by Zobrist key (see
//   TranspositionTable). Provides instant cutoffs on transpositions and the
//   best-move hint that drives move ordering.
// - Quiescence search at the horizon: instead of evaluating a position
//   mid-capture-sequence (the "horizon effect": depth 4 sees QxP and stops
//   before ...RxQ), leaf nodes keep searching captures until the position is
//   quiet, then evaluate.
// - Move ordering: TT move, MVV-LVA captures, killers, history (see MovePicker).
// - Late Move Reductions (LMR): quiet moves sorted far down the list are
//   almost never best, so they are first searched at reduced depth with a
//   null window; only if one surprisingly beats alpha is it re-searched at
//   full depth. This is what buys the extra plies.
// - Time management: the search stops when its time budget or a cancellation
//   is signalled, returning the best move of the last COMPLETED iteration.
//
// Still missing (per roadmap): PVS, null-move pruning, SEE (v1.0).
public sealed class AlphaBetaSearch(IPositionEvaluator evaluator)
{
    private const int Infinity = 1_000_000;
    public const int MateScore = 100_000;

    // Scores beyond this bound are mate scores and carry a distance-to-mate
    // component that must be adjusted when stored in / read from the TT.
    private const int MateBound = MateScore - 1_000;

    // Half a pawn on each side of the previous score. Wide enough that most
    // iterations stay inside, narrow enough to pay off.
    private const int AspirationWindow = 50;

    private const int MaxPly = 128;

    // How often (in nodes) the time/cancellation check runs. A power-of-two
    // mask makes the check nearly free.
    private const int StopCheckInterval = 2048;

    private readonly IPositionEvaluator _evaluator = evaluator;
    private readonly TranspositionTable _tt = new(sizeMb: 64);
    private readonly KillerTable _killers = new(MaxPly);
    private readonly HistoryTable _history = new();
    private readonly Stopwatch _timer = new();

    private long _nodes;
    private long _timeLimitMs;
    private CancellationToken _cancellation;

    // Set when the time budget or a cancellation fires. From that point every
    // node returns immediately and all partial scores are discarded: only the
    // last fully completed iteration is trusted.
    private bool _stopped;

    // Clears all inter-search state (TT, killers, history). Called on
    // "ucinewgame" / GUI new game: results from a previous game would still be
    // keyed correctly by Zobrist, but a fresh table avoids stale-depth noise.
    public void Reset()
    {
        _tt.Clear();
        _killers.Clear();
        _history.Clear();
    }

    public SearchResult FindBestMove(Board board, SearchLimits limits,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
    {
        if (limits.MaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Minimum depth is 1.");

        _nodes = 0;
        _stopped = false;
        _cancellation = cancellation;
        _timeLimitMs = limits.MaxTimeMs;
        _timer.Restart();

        // Killers are per-search (ply meanings change); history persists
        // between searches but decays so fresh information dominates.
        _killers.Clear();
        _history.Decay();

        SearchResult best = default;
        int previousScore = 0;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            CheckStop();
            if (_stopped)
                break;

            // Aspiration window around the previous iteration's score (only
            // once there is a reasonably stable score to aspire around).
            int alpha = depth >= 3 ? previousScore - AspirationWindow : -Infinity;
            int beta = depth >= 3 ? previousScore + AspirationWindow : Infinity;

            int score = SearchRoot(board, depth, alpha, beta, out Move bestMove);

            // Fail low/high: the true score is outside the window, so the
            // result is just a bound. Re-search this depth with a full window.
            if (!_stopped && (score <= alpha || score >= beta))
                score = SearchRoot(board, depth, -Infinity, Infinity, out bestMove);

            if (_stopped)
                break; // Interrupted iteration: keep the previous result.

            best = new SearchResult(bestMove, score, _nodes);
            previousScore = score;
            progress?.Report(new SearchProgress(depth, score, _nodes, bestMove));

            // A forced mate found: deeper iterations cannot improve it.
            if (Math.Abs(score) > MateBound)
                break;
        }

        // Extreme fallback (e.g. cancelled before depth 1 finished): never
        // return "no move" while legal moves exist — a GUI or match runner
        // must always receive a playable bestmove.
        if (best.BestMove == Move.None)
        {
            var legal = MoveGenerator.GenerateLegalMoves(board);
            if (legal.Count > 0)
                best = new SearchResult(legal[0], 0, _nodes);
        }

        return best;
    }

    // Root node. Separated from Negamax because it must track WHICH move is
    // best (inner nodes only need the score) and must never cut off on the TT
    // (we need a move, not just a bound).
    private int SearchRoot(Board board, int depth, int alpha, int beta, out Move bestMove)
    {
        bestMove = Move.None;

        var moves = MoveGenerator.GenerateLegalMoves(board);

        _tt.Probe(board.ZobristKey, out TTEntry entry);
        MovePicker.Order(moves, board, entry.BestMove, _killers, _history, ply: 0);

        int bestScore = -Infinity;

        foreach (Move move in moves)
        {
            board.MakeMove(move);
            int score = -Negamax(board, depth - 1, -beta, -alpha, ply: 1);
            board.UnmakeMove();

            // A score computed after the stop signal is garbage; only use it
            // if we have nothing at all yet.
            if (_stopped && bestMove != Move.None)
                break;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
                if (score > alpha)
                    alpha = score;
            }

            if (alpha >= beta)
                break;
        }

        if (!_stopped)
            _tt.Store(board.ZobristKey, depth, ToTT(bestScore, 0),
                      bestScore >= beta ? BoundType.LowerBound : BoundType.Exact, bestMove);

        return bestScore;
    }

    private int Negamax(Board board, int depth, int alpha, int beta, int ply)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        if (board.HalfmoveClock >= 100)
            return 0; // Draw by the fifty-move rule.

        // ---- Transposition table probe ----
        Move ttMove = Move.None;
        if (_tt.Probe(board.ZobristKey, out TTEntry entry))
        {
            ttMove = entry.BestMove; // Always useful for ordering.

            // The stored score is only reusable if it comes from a search at
            // least as deep as the one we are about to do, and its bound type
            // allows a conclusion within the current window.
            if (entry.Depth >= depth)
            {
                int ttScore = FromTT(entry.Score, ply);
                switch (entry.Bound)
                {
                    case BoundType.Exact:
                        return ttScore;
                    case BoundType.LowerBound when ttScore >= beta:
                        return ttScore;
                    case BoundType.UpperBound when ttScore <= alpha:
                        return ttScore;
                }
            }
        }

        // ---- Horizon: switch to quiescence instead of a raw evaluation ----
        if (depth <= 0)
            return Quiescence(board, alpha, beta, ply);

        bool inCheck = board.IsInCheck();

        var moves = MoveGenerator.GenerateLegalMoves(board);

        // No legal moves: mate or stalemate. The ply is added to the mate
        // score so the engine prefers the SHORTEST mate and, when mated,
        // drags it out as long as possible.
        if (moves.Count == 0)
            return inCheck ? -MateScore + ply : 0;

        MovePicker.Order(moves, board, ttMove, _killers, _history, ply);

        int originalAlpha = alpha;
        Move bestMove = Move.None;
        int bestScore = -Infinity;
        int searched = 0;

        foreach (Move move in moves)
        {
            bool isQuiet = !move.IsCapture && !move.IsPromotion;

            board.MakeMove(move);

            int score;

            // ---- Late Move Reductions ----
            // Quiet moves ranked far down the ordered list are rarely best.
            // Search them shallower with a null window (cheapest possible
            // refutation attempt); only re-search properly if they surprise.
            // Never reduce captures/promotions, while in check, or moves that
            // give check (tactical moves deserve full depth).
            if (searched >= 4 && depth >= 3 && isQuiet && !inCheck && !board.IsInCheck())
            {
                score = -Negamax(board, depth - 2, -alpha - 1, -alpha, ply + 1);
                if (score > alpha)
                    score = -Negamax(board, depth - 1, -beta, -alpha, ply + 1);
            }
            else
            {
                score = -Negamax(board, depth - 1, -beta, -alpha, ply + 1);
            }

            board.UnmakeMove();
            searched++;

            if (_stopped)
                return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;

                if (score > alpha)
                {
                    alpha = score;

                    if (alpha >= beta)
                    {
                        // Beta cutoff by a quiet move: exactly the signal the
                        // killer and history heuristics feed on.
                        if (isQuiet)
                        {
                            _killers.Store(ply, move);
                            _history.AddBonus(board.SideToMove, move, depth);
                        }
                        break;
                    }
                }
            }
        }

        // ---- Store the result in the TT with the right bound type ----
        BoundType bound = bestScore <= originalAlpha ? BoundType.UpperBound
                        : bestScore >= beta ? BoundType.LowerBound
                        : BoundType.Exact;
        _tt.Store(board.ZobristKey, depth, ToTT(bestScore, ply), bound, bestMove);

        return bestScore;
    }

    // Quiescence search: at the horizon, keep searching CAPTURES (and queen
    // promotions) until the position is quiet, then evaluate. This removes the
    // horizon effect: a depth-limited search would otherwise happily evaluate
    // a position right after QxP, never seeing the recapture ...RxQ one ply
    // beyond its horizon.
    private int Quiescence(Board board, int alpha, int beta, int ply)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        // "Stand pat": the side to move is never forced to capture, so the
        // static evaluation is a floor for its score. If even doing nothing
        // beats beta, the opponent will avoid this line — cut immediately.
        int standPat = _evaluator.Evaluate(board);
        if (standPat >= beta)
            return beta;
        if (standPat > alpha)
            alpha = standPat;
        if (ply >= MaxPly)
            return standPat;

        // Pseudo-legal generation + per-move legality check: cheaper than the
        // full legal generator when we only need a handful of captures.
        var moves = MoveGenerator.GeneratePseudoLegalMoves(board);
        moves.RemoveAll(m => !m.IsCapture && m.Flag != MoveFlag.PromoQueen);
        MovePicker.OrderCaptures(moves, board);

        Color us = board.SideToMove;

        foreach (Move move in moves)
        {
            board.MakeMove(move);

            // Discard moves that leave our own king in check.
            if (board.IsSquareAttacked(board.KingSquare(us), board.SideToMove))
            {
                board.UnmakeMove();
                continue;
            }

            int score = -Quiescence(board, -beta, -alpha, ply + 1);
            board.UnmakeMove();

            if (_stopped)
                return 0;

            if (score >= beta)
                return beta;
            if (score > alpha)
                alpha = score;
        }

        return alpha;
    }

    private void CheckStop()
    {
        if (_cancellation.IsCancellationRequested || _timer.ElapsedMilliseconds >= _timeLimitMs)
            _stopped = true;
    }

    // Mate scores encode the distance to mate from the ROOT ("mate in 5 plies
    // from where the search started"). Stored in the TT they must be relative
    // to the NODE instead, because the same position can be reached at
    // different plies from different roots. These two helpers convert between
    // both conventions when storing/probing.
    private static int ToTT(int score, int ply)
    {
        if (score > MateBound) return score + ply;
        if (score < -MateBound) return score - ply;
        return score;
    }

    private static int FromTT(int score, int ply)
    {
        if (score > MateBound) return score - ply;
        if (score < -MateBound) return score + ply;
        return score;
    }
}
