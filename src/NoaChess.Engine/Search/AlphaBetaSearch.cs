using System.Diagnostics;
using NoaChess.Core;
using NoaChess.Engine.Evaluation;
using NoaChess.Engine.Heuristics;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Search;

// Alpha-Beta search (negamax formulation), v1.0 feature set.
//
// On top of the v0.2 baseline (iterative deepening, aspiration windows,
// transposition table, quiescence, killers/history ordering, LMR):
//
// - PVS (Principal Variation Search): only the first move of each node is
//   searched with the full (alpha, beta) window. The rest get a "null window"
//   (alpha, alpha+1) — the cheapest possible way to prove "this move is NOT
//   better than what we already have", which is true for almost all of them.
//   Only the rare move that beats alpha is re-searched with the real window.
// - Null Move Pruning: before trying real moves, let the opponent move twice
//   in a row (we "pass"). If our position is STILL >= beta after that, it is
//   so good that the branch can be pruned. Disabled in check (passing is
//   illegal), in pawn endgames (zugzwang breaks the assumption) and twice in
//   a row (the position must be re-anchored to reality between passes).
// - Check extension: positions in check are searched one ply deeper — forced
//   sequences are cheap (few legal moves) and hide most tactics.
// - SEE pruning: losing captures (per static exchange evaluation) are skipped
//   near the horizon and ordered last elsewhere.
// - Repetition detection: a single repetition already scores as a draw inside
//   the search (if a position repeated once, nothing stops it repeating again).
// - Soft/hard time management: past the soft budget no new iteration starts;
//   past the hard deadline the search aborts and keeps the last completed one.
public sealed class AlphaBetaSearch(IPositionEvaluator evaluator)
{
    private const int Infinity = 1_000_000;
    public const int MateScore = 100_000;

    // Scores beyond this bound are mate scores and carry a distance-to-mate
    // component that must be adjusted when stored in / read from the TT.
    private const int MateBound = MateScore - 1_000;

    private const int MaxPly = 128;

    // Tunable search parameters (aspiration window width, LMR triggers...).
    // Selected via the UCI "Profile" option; see EngineProfile.
    public Profiles.EngineProfile Profile { get; set; } = Profiles.EngineProfile.Default;

    // How often (in nodes) the time/cancellation check runs. A power-of-two
    // mask makes the check nearly free.
    private const int StopCheckInterval = 2048;

    private IPositionEvaluator _evaluator = evaluator;

    // Set when the evaluator keeps incremental state (NNUE accumulators):
    // the search notifies it around every make/unmake. Null for stateless
    // evaluators (classical) — one branch-predicted null check per node.
    private IIncrementalEvaluator? _incremental = evaluator as IIncrementalEvaluator;

    private readonly TranspositionTable _tt = new(sizeMb: 64);
    private readonly KillerTable _killers = new(MaxPly);
    private readonly HistoryTable _history = new();
    private readonly Stopwatch _timer = new();

    // One reusable MoveList per ply (plus root and PV scratch lists): move
    // generation in the search allocates NOTHING. At any moment a given ply
    // has at most one active node, so sharing the list per ply is safe.
    private readonly MoveList[] _moveLists = CreateMoveLists();
    private readonly MoveList _rootMoves = new();
    private readonly MoveList _pvScratch = new();

    private static MoveList[] CreateMoveLists()
    {
        var lists = new MoveList[MaxPly + 2];
        for (int i = 0; i < lists.Length; i++)
            lists[i] = new MoveList();
        return lists;
    }

    // Late Move Reduction table: how many plies to shave off a quiet move that
    // is ranked late in a deep node. The reduction grows with BOTH the depth
    // and how far down the move order the move sits (a logarithmic product,
    // the standard shape). A move ordered 20th at depth 12 is almost certainly
    // not the best move, so it is searched much shallower first and only
    // re-searched at full depth if it surprisingly beats alpha.
    private static readonly int[,] LmrReductions = BuildLmrTable();

    private static int[,] BuildLmrTable()
    {
        var table = new int[64, 64];
        for (int depth = 1; depth < 64; depth++)
            for (int move = 1; move < 64; move++)
                table[depth, move] = (int)(0.75 + Math.Log(depth) * Math.Log(move) / 2.25);
        return table;
    }

    private long _nodes;
    private long _hardTimeMs;
    private long _maxNodes;
    private CancellationToken _cancellation;

    // Set when the hard deadline, the node cap or a cancellation fires. From
    // that point every node returns immediately and all partial scores are
    // discarded: only the last fully completed iteration is trusted.
    private bool _stopped;

    // Reallocates the transposition table ("setoption name Hash value N").
    public void ResizeTT(int sizeMb) => _tt.Resize(sizeMb);

    // Clears all inter-search state (TT, killers, history). Called on
    // "ucinewgame" / GUI new game.
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
        _softStopped = false;
        _cancellation = cancellation;
        _hardTimeMs = limits.HardTimeMs;
        _maxNodes = limits.MaxNodes;
        _timer.Restart();

        // Clock mode is recognizable by soft < hard; "movetime" sets them
        // equal and the budget must then be used in full, not predictively.
        bool clockMode = limits.SoftTimeMs < limits.HardTimeMs;

        // Forced move: with a single legal reply no amount of searching can
        // change the choice — answer instantly and bank the whole budget.
        // Only under a clock (analysis/movetime callers still want the eval).
        if (clockMode)
        {
            MoveGenerator.GenerateLegalMoves(board, _rootMoves);
            if (_rootMoves.Count == 1)
                return new SearchResult(_rootMoves[0], 0, 0);
        }

        // Killers are per-search (ply meanings change); history persists
        // between searches but decays so fresh information dominates.
        _killers.Clear();
        _history.Decay();

        // Anchor the incremental evaluator's state (NNUE accumulators) at
        // the new root position.
        _incremental?.Reset(board);

        SearchResult best = default;
        int previousScore = 0;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            CheckStop();
            if (_stopped)
                break;

            // Soft budget: starting an iteration we most likely cannot finish
            // is wasted time — better to move now and save the clock.
            if (depth > 1 && _timer.ElapsedMilliseconds >= limits.SoftTimeMs)
                break;

            // Aspiration window around the previous iteration's score (only
            // once there is a reasonably stable score to aspire around).
            int window = Profile.AspirationWindow;
            int alpha = depth >= 3 ? previousScore - window : -Infinity;
            int beta = depth >= 3 ? previousScore + window : Infinity;

            int score = SearchRoot(board, depth, alpha, beta, out Move bestMove);

            // Fail low/high: the true score is outside the window, so the
            // result is just a bound. Re-search this depth with a full window.
            if (!_stopped && !_softStopped && (score <= alpha || score >= beta))
                score = SearchRoot(board, depth, -Infinity, Infinity, out bestMove);

            if (_stopped)
                break; // Hard interruption: keep the previous result.

            if (_softStopped)
            {
                // Soft cut mid-iteration: the moves searched so far (starting
                // with the previous iteration's best, thanks to TT ordering)
                // were searched completely — their best is at least as good
                // as the previous iteration's answer. Use it and stop.
                if (bestMove != Move.None)
                    best = new SearchResult(bestMove, score, _nodes);
                break;
            }

            best = new SearchResult(bestMove, score, _nodes);
            previousScore = score;
            progress?.Report(new SearchProgress(depth, score, _nodes, bestMove,
                                                ExtractPv(board, bestMove, depth)));

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

        MoveList moves = _rootMoves;
        MoveGenerator.GenerateLegalMoves(board, moves);

        _tt.Probe(board.ZobristKey, out TTEntry entry);
        MovePicker.Order(moves, board, entry.BestMove, _killers, _history, ply: 0);

        int bestScore = -Infinity;
        int searched = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            _incremental?.PushMove(board, move);
            board.MakeMove(move);

            // PVS at the root: first move with the full window, the rest with
            // a null window plus re-search when they surprise.
            int score;
            if (searched == 0)
            {
                score = -Negamax(board, depth - 1, -beta, -alpha, ply: 1, allowNull: true);
            }
            else
            {
                score = -Negamax(board, depth - 1, -alpha - 1, -alpha, ply: 1, allowNull: true);
                if (score > alpha && !_stopped)
                    score = -Negamax(board, depth - 1, -beta, -alpha, ply: 1, allowNull: true);
            }

            board.UnmakeMove();
            searched++;

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

            // Soft time boundary: root moves are the only place where the
            // search can stop "gracefully" — everything searched so far is
            // complete and usable. Requires at least one searched move and
            // never fires at depth 1 (a full depth-1 pass costs nothing and
            // guarantees a sane fallback move).
            if (depth > 1 && bestMove != Move.None
                && _timer.ElapsedMilliseconds >= _softTimeMs)
            {
                _softStopped = true;
                break;
            }
        }

        // A partial (soft-stopped) iteration must not be recorded in the TT
        // as if the position had been fully searched at this depth.
        if (!_stopped && !_softStopped)
            _tt.Store(board.ZobristKey, depth, ToTT(bestScore, 0),
                      bestScore >= beta ? BoundType.LowerBound : BoundType.Exact, bestMove);

        return bestScore;
    }

    private int Negamax(Board board, int depth, int alpha, int beta, int ply, bool allowNull)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        // ---- Draws by rule. Checked before the TT: a cached score cannot
        //      know how many times THIS game path repeated the position. ----
        if (board.HalfmoveClock >= 100 || board.CountRepetitions() >= 1)
            return 0;

        bool inCheck = board.IsInCheck();

        // Check extension: forced positions are cheap to search (few legal
        // replies) and hide most tactics — give them one extra ply.
        if (inCheck)
            depth++;

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

        // Non-PV nodes are searched with a null window (beta == alpha + 1);
        // the aggressive prunings below only fire there, never on the principal
        // variation where a wrong cut would corrupt the reported line.
        bool nonPv = beta - alpha == 1;

        // Static evaluation, reused by the forward-pruning heuristics. Skipped
        // in check (the position is not "quiet" and the eval is meaningless).
        int staticEval = inCheck ? 0 : _evaluator.Evaluate(board);

        // ---- Reverse futility pruning (a.k.a. static null move) ----
        // If our static eval is so far above beta that even conceding a healthy
        // margin per remaining ply keeps us above it, the opponent will avoid
        // this line — return without searching. Only at shallow depth and away
        // from mate scores, where the static eval is a trustworthy proxy.
        if (!inCheck && nonPv && depth <= 6 && Math.Abs(beta) < MateBound
            && staticEval - 85 * depth >= beta)
            return staticEval;

        // ---- Null Move Pruning ----
        // "Pass" the turn: if the opponent moving twice in a row still cannot
        // bring us below beta, no real move will either — prune the branch.
        // Skipped: in check (passing is illegal), without non-pawn material
        // (zugzwang), right after another null move, and at reduced depths
        // where the verification search would be meaningless.
        if (allowNull && !inCheck && depth >= 3 && ply > 0
            && board.HasNonPawnMaterial(board.SideToMove))
        {
            int reduction = 2 + depth / 4;
            board.MakeNullMove();
            int nullScore = -Negamax(board, depth - 1 - reduction, -beta, -beta + 1,
                                     ply + 1, allowNull: false);
            board.UnmakeNullMove();

            if (_stopped)
                return 0;
            if (nullScore >= beta && nullScore < MateBound)
                return beta;
        }

        MoveList moves = _moveLists[ply];
        MoveGenerator.GenerateLegalMoves(board, moves);

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
        int quietsSearched = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            bool isQuiet = !move.IsCapture && !move.IsPromotion;

            // ---- Forward pruning of quiet moves (shallow, non-PV, not in
            //      check, at least one move already searched so a best move is
            //      guaranteed) ----
            if (isQuiet && searched > 0 && nonPv && !inCheck && Math.Abs(alpha) < MateBound)
            {
                // Late move pruning: once enough quiet moves have been tried at
                // low depth, the remaining ones are very unlikely to be best.
                if (depth <= 3 && quietsSearched >= 3 + depth * depth)
                    continue;

                // Futility pruning: if even a generous per-ply margin over the
                // static eval cannot lift it to alpha, a quiet move will not
                // rescue the node — skip it.
                if (depth <= 4 && staticEval + 100 * depth <= alpha)
                    continue;
            }

            // ---- SEE pruning near the horizon ----
            // A capture that clearly loses material (per static exchange
            // evaluation) will not recover the loss in the couple of plies
            // left; skip it. Never prunes the first move (something must be
            // searched) nor while in check.
            if (depth <= 2 && searched > 0 && !inCheck
                && move.IsCapture && !move.IsPromotion
                && StaticExchangeEvaluator.Evaluate(board, move) < -100)
            {
                continue;
            }

            board.MakeMove(move);

            int score;

            if (searched == 0)
            {
                // PVS: the first (best-ordered) move gets the full window.
                score = -Negamax(board, depth - 1, -beta, -alpha, ply + 1, allowNull: true);
            }
            else
            {
                // ---- Late Move Reductions ----
                // Quiet moves ranked far down the ordered list are rarely
                // best: probe them several plies shallower (amount from the
                // logarithmic LmrReductions table), then re-search at full
                // depth only if the probe beats alpha. Tactical moves, checks
                // and check evasions always get full depth. The trigger
                // thresholds come from the active profile (Bullet reduces
                // sooner); board.IsInCheck() here means "the move gives check".
                int reduction = 0;
                if (isQuiet && searched >= Profile.LmrMinMoves && depth >= Profile.LmrMinDepth
                    && !inCheck && !board.IsInCheck())
                {
                    reduction = LmrReductions[Math.Min(depth, 63), Math.Min(searched, 63)];
                    if (nonPv) reduction++;              // Reduce harder off the PV.
                    if (reduction < 0) reduction = 0;
                    if (reduction > depth - 1) reduction = depth - 1;
                }

                // PVS null window (cheap refutation attempt), possibly reduced.
                score = -Negamax(board, depth - 1 - reduction, -alpha - 1, -alpha,
                                 ply + 1, allowNull: true);

                // The reduced probe beat alpha: verify at full depth first.
                if (score > alpha && reduction > 0 && !_stopped)
                    score = -Negamax(board, depth - 1, -alpha - 1, -alpha,
                                     ply + 1, allowNull: true);

                // Still inside the window: it is a genuine PV candidate,
                // re-search with the real window.
                if (score > alpha && score < beta && !_stopped)
                    score = -Negamax(board, depth - 1, -beta, -alpha,
                                     ply + 1, allowNull: true);
            }

            board.UnmakeMove();
            _incremental?.Pop();
            searched++;
            if (isQuiet)
                quietsSearched++;

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

        // Every move may have been SEE-pruned except the first; bestMove is
        // then still valid (the first move is never pruned).

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

        // Captures-only pseudo-legal generation + per-move legality check:
        // much cheaper than the full legal generator, and quiescence nodes
        // are the majority of all search nodes.
        MoveList moves = _moveLists[ply];
        MoveGenerator.GeneratePseudoLegalMoves(board, moves, capturesOnly: true);
        MovePicker.OrderCaptures(moves, board);

        Color us = board.SideToMove;

        for (int i = 0; i < moves.Count; i++)
        {
            // SEE pruning: a losing capture cannot raise the stand-pat floor —
            // in quiescence there is no compensation coming later.
            if (move.IsCapture && !move.IsPromotion
                && StaticExchangeEvaluator.Evaluate(board, move) < 0)
            {
                continue;
            }

            board.MakeMove(move);

            // Discard moves that leave our own king in check.
            if (board.IsSquareAttacked(board.KingSquare(us), board.SideToMove))
            {
                board.UnmakeMove();
                _incremental?.Pop();
                continue;
            }

            int score = -Quiescence(board, -beta, -alpha, ply + 1);
            board.UnmakeMove();
            _incremental?.Pop();

            if (_stopped)
                return 0;

            if (score >= beta)
                return beta;
            if (score > alpha)
                alpha = score;
        }

        return alpha;
    }

    // Reconstructs the principal variation (the expected best play for both
    // sides) by walking the transposition table: play the best move, look up
    // the resulting position's stored best move, and repeat. The PV may be
    // shorter than the search depth when TT entries were overwritten. Each
    // stored move is validated against the legal moves of the position — a
    // TT index collision could otherwise inject a corrupt move.
    private Move[] ExtractPv(Board board, Move firstMove, int maxLength)
    {
        var pv = new List<Move>(maxLength) { firstMove };
        board.MakeMove(firstMove);
        int made = 1;

        while (pv.Count < maxLength
               && _tt.Probe(board.ZobristKey, out TTEntry entry)
               && entry.BestMove != Move.None
               && MoveGenerator.GenerateLegalMoves(board).Contains(entry.BestMove))
        {
            pv.Add(entry.BestMove);
            board.MakeMove(entry.BestMove);
            made++;
        }

        while (made-- > 0)
            board.UnmakeMove();

        return [.. pv];
    }

    private void CheckStop()
    {
        if (_cancellation.IsCancellationRequested
            || _timer.ElapsedMilliseconds >= _hardTimeMs
            || _nodes >= _maxNodes)
        {
            _stopped = true;
        }
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
