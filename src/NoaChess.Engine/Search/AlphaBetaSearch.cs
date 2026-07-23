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
// - SEE pruning: losing captures (per static exchange evaluation) are skipped
//   near the horizon and ordered last elsewhere.
// - Repetition detection: a single repetition already scores as a draw inside
//   the search (if a position repeated once, nothing stops it repeating again).
// - Adaptive time management: TimeManager supplies an optimum and a maximum
//   time; each completed iteration re-modulates the optimum by falling-eval,
//   best-move-stability and best-move-instability factors and the search stops
//   past the modulated budget (gracefully at root-move boundaries, keeping the
//   partial iteration's best). Past the maximum the search aborts outright.
public sealed class AlphaBetaSearch(IPositionEvaluator evaluator)
{
    private const int Infinity = 1_000_000;
    public const int MateScore = 100_000;

    // Scores beyond this bound are mate scores and carry a distance-to-mate
    // component that must be adjusted when stored in / read from the TT.
    private const int MateBound = MateScore - 1_000;

    // ---- Syzygy tablebase score band ----
    // A tablebase verdict is certain, so it must outrank every heuristic
    // evaluation, but it is NOT a mate score: reporting it as one would make
    // the engine claim a forced mate it has not proven and would corrupt the
    // mate-distance arithmetic. It therefore sits in its own band just below
    // the mate range. The ply term makes a win found sooner preferable.
    public const int TbWin = MateBound - MaxPly;

    // Lowest score that still belongs to the decisive tablebase band. Like
    // mate scores, TB scores carry a root-ply component and must be converted
    // when they cross the TT boundary.
    private const int TbScoreBound = TbWin - MaxPly;

    // Scale used by the reference root DTZ ranking. It leaves separate bands
    // for certain wins/losses and outcomes affected by the fifty-move rule.
    private const int MaxDtz = 1 << 18;

    /// Set from the UCI SyzygyProbeLimit / SyzygyProbeDepth options.
    public int SyzygyProbeLimit
    {
        get => _syzygyProbeLimit;
        set { _syzygyProbeLimit = value; RefreshTbLimit(); }
    }
    private int _syzygyProbeLimit = 7;

    // Largest piece count worth probing: the smaller of the option and what is
    // actually loaded, or 0 when there are no tablebases at all.
    private int _tbMaxMen;
    private int _tbMinProbeDepth = 1;

    /// Recomputed after the tablebases are (re)loaded or the limit changes.
    public void RefreshTbLimit()
    {
        if (!Tablebases.Syzygy.Available)
        {
            _tbMaxMen = 0;
            _tbMinProbeDepth = _syzygyProbeDepth;
            return;
        }

        _tbMaxMen = Math.Min(_syzygyProbeLimit, Tablebases.Syzygy.Cardinality);

        // Matching reference: if the requested limit exceeds the largest
        // installed table, that installed cardinality is effectively a
        // sub-cardinality table and is therefore probed at every depth.
        _tbMinProbeDepth = _syzygyProbeLimit > Tablebases.Syzygy.Cardinality
            ? 0 : _syzygyProbeDepth;
    }

    public int SyzygyProbeDepth
    {
        get => _syzygyProbeDepth;
        set { _syzygyProbeDepth = value; RefreshTbLimit(); }
    }
    private int _syzygyProbeDepth = 1;
    public bool Syzygy50MoveRule { get; set; } = true;

    /// Number of positions this search resolved from tablebases.
    public long TbHits { get; private set; }

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
    private readonly ContinuationHistory _contHist = new();
    private readonly CaptureHistory _captureHistory = new();
    private readonly PawnCorrectionHistory _pawnCorrectionHistory = new();
    private readonly Stopwatch _timer = new();

    // ---- Quiescence pruning constants (reference Step 6) ----
    // The reference's values live in ITS units, where a pawn is 208; ours is
    // 100, so both are converted by that ratio (the project's x0.48 rule,
    // which is exactly 100/208). Margin 306 -> 147, SEE floor -74 -> -36.
    private const int QsFutilityMargin = 147;
    private const int QsSeeThreshold = 36; // LosesAtLeast takes it positive.

    // Victim values for the quiescence futility margin, in our own units.
    private static readonly int[] PieceValueQs = [100, 320, 330, 500, 900, 0, 0];

    // Counter move: the quiet refutation of the opponent's last move, indexed
    // by (mover piece 0-11, destination). Cheaper and more specific than a
    // killer: it follows the MOVE being answered, not the ply.
    private readonly Move[,] _counterMoves = new Move[12, 64];

    // Search stack: piece index (color*6+type) and destination of the move
    // played to REACH each ply. -1 piece marks "no usable previous move"
    // (a null move); continuation history and counter moves then skip it.
    private readonly int[] _stackPiece = new int[MaxPly + 2];
    private readonly int[] _stackTo   = new int[MaxPly + 2];
    // Static eval at each ply (sentinel NoEval when in check). Used to derive
    // the improving flag: eval[ply] > eval[ply-2] means our position is trending
    // upward, which gates several pruning and reduction heuristics.
    private const int NoEval = int.MinValue / 2;
    private readonly int[] _stackEval = new int[MaxPly + 2];

    // statScore of the move played to REACH each ply: 2x butterfly history plus
    // the move's continuation history, minus an offset — in OUR history units.
    // The child consults [ply-1]: a parent move the tables love means the
    // parent line keeps refuting things, so the child skips NMP (the fail-high
    // is already cheap without a null probe) and its RFP margin leans on it.
    private readonly int[] _stackStatScore = new int[MaxPly + 2];

    // statScore-derived thresholds. The reference values are in ITS history
    // units (tables gravity-capped at 14365/29952); ours accumulate depth^2
    // with far smaller magnitudes (measured 2026-07-17: butterfly p99 3218,
    // contHist p99 630 — combined statScore range ~0.28x the reference's), so
    // every threshold is scaled by that measured ratio, and value-producing
    // divisors additionally by the x0.48 value-unit rule.
    private const int StatScoreOffset = 1250; // reference  4433 x 0.28
    private const int StatScoreRfpDiv = 180;  // reference 303 / 0.48 x 0.28

    // ProbCut safety margins. As in the reference search, an improving node
    // gets both a cheaper bar and a shallower verification: the static trend
    // is treated as extra confidence, reducing ProbCut's cost.
    private const int ProbCutMargin = 150;
    private const int ProbCutImprovingMargin = 40;
    private const int SmallProbCutMargin = 428;
    // NMP verification-search state (reference nmpMinPly/nmpColor): while the
    // verification search runs, null moves stay disabled for the verifying
    // side below this ply, so a false null-move cutoff cannot verify itself.
    private int _nmpMinPly;
    private Color _nmpColor;


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

    // Reductions are accumulated in 1024ths of a ply and truncated once, at the
    // point of use. The reference keeps its whole reduction pipeline in fixed
    // point for a reason: every one of its adjusters is a FRACTION of a ply.
    // Truncating per term — which an integer table forces — makes each adjuster
    // three to ten times too coarse, and eight of them stack into swings the
    // reference never applies. That granularity is the unnamed "ecosystem" the
    // 5C adjuster suite kept measuring against.
    private const int LmrScale = 1024;


    private static int[,] BuildLmrTable()
    {
        var table = new int[64, 64];
        for (int depth = 1; depth < 64; depth++)
            for (int move = 1; move < 64; move++)
                table[depth, move] =
                    (int)((0.75 + Math.Log(depth) * Math.Log(move) / 2.25) * LmrScale);
        return table;
    }

    private long _nodes;
    private long _hardTimeMs;
    private long _maxNodes;
    private CancellationToken _cancellation;

    // Time already spent against this move's budget before the search started
    // (pondering time on a ponderhit relaunch). Added to every elapsed-time
    // check so the budget spans go-ponder -> ponderhit -> move, like the
    // reference scheduler.
    private long _elapsedOffsetMs;
    private long ElapsedMs => _timer.ElapsedMilliseconds + _elapsedOffsetMs;

    // ---- Adaptive time management state (v2.6.5) ----
    // The per-move budget from TimeManager is the OPTIMUM time; every
    // completed iteration re-modulates it into the actual deadline
    // (optimum x fallingEval x reduction x bestMoveInstability). This is the
    // dynamic part of a top engine's scheduler: stable searches with a rising
    // eval stop at about half the optimum, while a falling eval or a flapping
    // best move extends the think up to ~3x.

    // Deadline used by the iteration/root-boundary soft checks. Starts at the
    // optimum and is re-derived after each completed iteration (clock mode).
    private long _softDeadlineMs;

    // Root best-move changes during the current iteration (root fills it).
    private int _bestMoveChanges;

    // Sentinel for "no previous score yet" (first search of the game).
    private const int ScoreNone = int.MaxValue / 2;

    // Cross-move state (persists between searches, cleared on new game):
    // the previous move's score/average score, the previous move's stability
    // factor and the last four iteration scores of the previous search.
    private int _bestPreviousScore = ScoreNone;
    private int _bestPreviousAverageScore = ScoreNone;
    private double _previousTimeReduction = 1.0;
    private readonly int[] _iterValue = new int[4];

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
        _contHist.Clear();
        _captureHistory.Clear();
        _pawnCorrectionHistory.Clear();
        Array.Clear(_counterMoves);
        _bestPreviousScore = ScoreNone;
        _bestPreviousAverageScore = ScoreNone;
        _previousTimeReduction = 1.0;
    }

    public SearchResult FindBestMove(Board board, SearchLimits limits,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
    {
        if (limits.MaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Minimum depth is 1.");

        _nodes = 0;
        TbHits = 0;
        _stopped = false;
        _softStopped = false;
        _cancellation = cancellation;
        _hardTimeMs = limits.HardTimeMs;
        _softTimeMs = limits.SoftTimeMs;
        _softDeadlineMs = limits.SoftTimeMs;
        _maxNodes = limits.MaxNodes;
        _elapsedOffsetMs = limits.ElapsedOffsetMs;
        _timer.Restart();

        // Clock mode is recognizable by soft < hard; "movetime" sets them
        // equal and the budget must then be used in full, not predictively.
        bool clockMode = limits.SoftTimeMs < limits.HardTimeMs;

        // Terminal root: checkmate or stalemate on the board. There is nothing
        // to search and, crucially, nothing to return — the iterative-deepening
        // loop below would spin through every depth without ever producing a
        // best move, and the caller would wait forever for a "bestmove" that
        // never comes. Answer at once with the game-theoretic score and a null
        // move; the UCI layer turns that into "bestmove 0000".
        // (Measured 2026-07-19: v2.7.2 and every earlier release hang outright
        // on a stalemated position — a GUI sending one froze the engine.)
        MoveGenerator.GenerateLegalMoves(board, _rootMoves);
        if (_rootMoves.Count == 0)
            return new SearchResult(Move.None, board.IsInCheck() ? -MateScore : 0, 0);
        int legalRootMoveCount = _rootMoves.Count;

        // ---- Syzygy root filtering ----
        // Knowing the position is won is not enough to WIN it: with no distance
        // to steer by the engine shuffles and draws by the fifty-move rule. DTZ
        // supplies that gradient. The root move list is therefore restricted to
        // the tablebase-optimal moves — win > draw > loss, and among wins the
        // shortest distance to the next irreversible move.
        //
        // Deliberately a FILTER and not an early return. Returning the verdict
        // straight away would replace "mate in 3" with a plain tablebase win in
        // the UCI output, undoing the mate reporting added in v2.7.1. Filtering
        // keeps the search running — so it still finds and announces the mate —
        // while making it structurally impossible to play a move that throws
        // the win away.
        if (Tablebases.Syzygy.Available
            && board.CastlingRights == CastlingRights.None
            && System.Numerics.BitOperations.PopCount(board.AllOccupancy)
               <= Math.Min(SyzygyProbeLimit, Tablebases.Syzygy.Cardinality))
        {
            FilterRootMovesByTablebase(board);
        }

        // Forced move: with a single legal reply no amount of searching can
        // change the choice — answer instantly and bank the whole budget.
        // Only under a clock (analysis/movetime callers still want the eval).
        if (clockMode && legalRootMoveCount == 1)
            return new SearchResult(_rootMoves[0], 0, 0);

        // Killers are per-search (ply meanings change); history persists
        // between searches but decays so fresh information dominates. The TT
        // ages one generation: previous-search entries yield their cluster
        // slots gracefully as this search fills the table.
        _killers.Clear();
        _history.Decay();
        _tt.NewSearch();

        // Per-search NMP verification state and statScore stack (stale scores
        // from the previous search describe other positions).
        _nmpMinPly = 0;
        Array.Clear(_stackStatScore);

        // Anchor the incremental evaluator's state (NNUE accumulators) at
        // the new root position.
        _incremental?.Reset(board);

        SearchResult best = default;
        int previousScore = 0;

        // ---- Dynamic time management (per-search state) ----
        // Exponentially decayed count of root best-move changes: a search that
        // keeps flapping between root moves needs more time to settle.
        double totBestMoveChanges = 0;
        // Stability factor carried to the NEXT move via _previousTimeReduction.
        double timeReduction = 1.0;
        // The last completed-iteration best move and the depth where it last
        // changed ("stable for 10 iterations" halves the budget).
        Move lastBestMove = Move.None;
        int lastBestMoveDepth = 0;
        // Running average of the best score across iterations (weights recent
        // iterations 2:1), carried to the next move for the falling-eval term.
        int averageScore = ScoreNone;
        // Ring buffer index over the previous 4 iteration scores.
        int iterIdx = 0;
        // Seed the iteration scores with the previous move's score so the
        // falling-eval factor reacts to drops ACROSS moves, not only within
        // this search. First move of the game: no history, seeded with 0 and
        // the sentinel keeps the factor neutral (see the fallingEval note
        // below).
        int seed = _bestPreviousScore == ScoreNone ? 0 : _bestPreviousScore;
        for (int i = 0; i < _iterValue.Length; i++)
            _iterValue[i] = seed;

        for (int depth = 1; depth <= limits.MaxDepth; depth++)
        {
            CheckStop();
            if (_stopped)
                break;

            // Age out the best-move variability metric and restart the
            // per-iteration change counter (SearchRoot increments it).
            totBestMoveChanges /= 2;
            _bestMoveChanges = 0;

            // Aspiration window around the previous iteration's score (only
            // once there is a reasonably stable score to aspire around).
            // On a fail the window is re-centered on the failing score and
            // DOUBLED, instead of jumping straight to a full-width re-search:
            // most fails land just outside the window, so the progressive
            // widening usually resolves them in one cheap retry.
            // The fixed profile window won the final v2.8.2 SPRT. Adaptive
            // narrowing increased re-search cost at short time controls.
            int window = Profile.AspirationWindow;
            int alpha = depth >= 3 ? previousScore - window : -Infinity;
            int beta = depth >= 3 ? previousScore + window : Infinity;

            int score;
            Move bestMove;
            while (true)
            {
                score = SearchRoot(board, depth, alpha, beta, out bestMove);
                if (_stopped || _softStopped || (score > alpha && score < beta))
                    break;

                if (score <= alpha)
                {
                    // Keep the upper edge near the failed window instead of
                    // carrying a needlessly high beta into the re-search.
                    beta = alpha + (beta - alpha) / 2;
                    alpha = Math.Max(score - window, -Infinity);
                }
                else
                {
                    beta = Math.Min(score + window, Infinity);
                }

                window *= 2;
                if (window > 1000) // Give up widening: full window.
                {
                    alpha = -Infinity;
                    beta = Infinity;
                }
            }

            if (_stopped || _softStopped)
            {
                // Interrupted mid-iteration. The moves searched so far
                // (starting with the previous iteration's best, thanks to TT
                // ordering) were searched completely — their best is at least
                // as good as the previous iteration's answer. Use it and stop.
                if (bestMove != Move.None)
                    best = new SearchResult(bestMove, score, _nodes);
                break;
            }

            best = new SearchResult(bestMove, score, _nodes);
            previousScore = score;
            progress?.Report(new SearchProgress(depth, score, _nodes, bestMove,
                                                ExtractPv(board, bestMove, depth)));

            // Never stop deepening on a mate score. When MATED, deeper
            // iterations find longer defenses or refute the mate entirely
            // (stopping here made the engine walk into the SHORTEST mate:
            // it played the first shallow defense instead of, e.g., trading
            // into a mated-in-8 rook ending it could only see at d16+).
            // When MATING, deeper iterations find shorter mates. The
            // reference engine never breaks on mate scores either; the
            // clock is what ends the search.

            // ---- Dynamic per-iteration budget (clock mode only) ----
            if (bestMove != lastBestMove)
            {
                lastBestMove = bestMove;
                lastBestMoveDepth = depth;
            }
            totBestMoveChanges += _bestMoveChanges;
            averageScore = averageScore == ScoreNone ? score : (2 * score + averageScore) / 3;

            if (clockMode)
            {
                // Falling eval: when the score is dropping against the
                // previous move's average and the recent iterations, think
                // longer (the position is deteriorating and the move matters);
                // rising scores stop sooner. Constants are the reference
                // engine's with score differences rescaled to NoaChess
                // centipawns (x2.08: its internal pawn ~ 208).
                // First move of the game: no cross-move history to compare
                // against, so use the neutral factor. (The reference maxes it
                // at 1.5 here; combined with the early-depth reduction factor
                // ~1.7 that tripled the first-move budget — visible clock
                // burn at short TC with no upside on an empty TT.)
                double fallingEval = _bestPreviousAverageScore == ScoreNone
                    ? 1.0
                    : Math.Clamp((71 + 25.0 * (_bestPreviousAverageScore - score)
                                     + 12.5 * (_iterValue[iterIdx] - score)) / 656.7,
                                 0.5, 1.5);

                // Stability: if the best move has not changed for 10
                // iterations the position is decided — spend less; the factor
                // also carries over to the next move via previousTimeReduction.
                timeReduction = lastBestMoveDepth + 9 < depth ? 1.37 : 0.65;
                double reduction = (1.4 + _previousTimeReduction) / (2.15 * timeReduction);

                // Instability: each root best-move change (decayed per
                // iteration) extends the budget. Neutral on the first move of
                // the game: on an empty TT the root flaps between near-equal
                // openings, and that flapping carries no urgency signal —
                // extending for it just burns the clock before the game starts.
                double bestMoveInstability = _bestPreviousAverageScore == ScoreNone
                    ? 1.0
                    : 1 + 1.7 * totBestMoveChanges;

                double totalTime = _softTimeMs * fallingEval * reduction * bestMoveInstability;

                // Stop if past the modulated budget; otherwise it becomes the
                // deadline the next iteration's root-boundary checks use.
                if (ElapsedMs > totalTime)
                    break;
                _softDeadlineMs = (long)totalTime;
            }

            _iterValue[iterIdx] = score;
            iterIdx = (iterIdx + 1) & 3;
        }

        // Carry the scheduler state to the next move of the game.
        if (clockMode)
        {
            _previousTimeReduction = timeReduction;
            if (best.BestMove != Move.None)
            {
                _bestPreviousScore = best.Score;
                _bestPreviousAverageScore = averageScore == ScoreNone ? best.Score : averageScore;
            }
        }

        // Extreme fallback (e.g. cancelled before depth 1 finished — a cold
        // process under a tiny first-move budget): never return "no move" while
        // legal moves exist. Instead of the FIRST generated move (move ordering
        // makes that a rook-pawn push, which looks absurd), pick the move with
        // the best static eval — a one-ply search that costs one eval per legal
        // move and guarantees a sane reply even when the real search never ran.
        if (best.BestMove == Move.None)
        {
            MoveList legal = _rootMoves;
            if (legal.Count > 0)
            {
                Move fallbackMove = legal[0];
                int fallbackVal = int.MinValue;
                for (int i = 0; i < legal.Count; i++)
                {
                    _incremental?.PushMove(board, legal[i]);
                    board.MakeMove(legal[i]);
                    int val = -_evaluator.Evaluate(board); // child is opponent-relative
                    board.UnmakeMove();
                    _incremental?.Pop();
                    if (val > fallbackVal)
                    {
                        fallbackVal = val;
                        fallbackMove = legal[i];
                    }
                }
                best = new SearchResult(fallbackMove, fallbackVal, _nodes);
            }
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

        _tt.Probe(board.ZobristKey, out TTEntry entry);
        MovePicker.Order(moves, board, entry.BestMove, _killers, _history, ply: 0,
            contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None,
            captureHistory: _captureHistory);

        int bestScore = -Infinity;
        int searched = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            _stackPiece[0] = ContinuationHistory.PieceIndex(board.SideToMove, board.PieceTypeAt(move.From));
            _stackTo[0] = move.To;
            _stackStatScore[0] = (move.IsCapture || move.IsPromotion ? 0
                : 2 * _history.Get(board.SideToMove, move)) - StatScoreOffset;
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

                // Best-move change bookkeeping for the time manager: a root
                // move other than the first one taking over the lead signals
                // an unstable position that deserves more time.
                if (searched > 1)
                    _bestMoveChanges++;

                if (score > alpha)
                    alpha = score;
            }

            if (alpha >= beta)
                break;

            // Soft time boundary: root moves are the only place where the
            // search can stop "gracefully" — everything searched so far is
            // complete and usable. The deadline is the dynamically modulated
            // budget from the previous iteration (see FindBestMove). Requires
            // at least one searched move and never fires at depth 1 (a full
            // depth-1 pass costs nothing and guarantees a sane fallback move).
            if (depth > 1 && bestMove != Move.None
                && ElapsedMs >= _softDeadlineMs)
            {
                _softStopped = true;
                break;
            }
        }

        // A partial (soft-stopped) iteration must not be recorded in the TT
        // as if the position had been fully searched at this depth.
        if (!_stopped && !_softStopped)
            _tt.Store(board.ZobristKey, depth, ToTT(bestScore, 0), TTEntry.NoStaticEval,
                      bestScore >= beta ? BoundType.LowerBound : BoundType.Exact, bestMove,
                      isPv: true);

        return bestScore;
    }

    // Restricts _rootMoves to the tablebase-optimal ones. DTZ is expressed from
    // the ROOT position: a move that zeroes the counter has distance 1, while a
    // reversible move adds one ply to the child's DTZ. The rank then separates
    // certain wins/losses from outcomes affected by rule 50, exactly as the
    // reference does. If DTZ data is incomplete, WDL still prevents the search
    // from choosing a move with a worse game-theoretic result. If either full
    // pass succeeds, all equally optimal moves remain available to the search.
    private void FilterRootMovesByTablebase(Board board)
    {
        int n = _rootMoves.Count;
        if (n <= 1)
            return;

        Span<int> ranks = stackalloc int[n];
        if (!TryRankRootMovesByDtz(board, ranks, out int bestRank)
            && !TryRankRootMovesByWdl(board, ranks, out bestRank))
            return;

        TbHits++;

        // Keep every move with the best TB rank, so the search still gets a
        // choice among equally optimal continuations.
        var keep = new MoveList();
        for (int i = 0; i < n; i++)
            if (ranks[i] == bestRank)
                keep.Add(_rootMoves[i]);

        if (keep.Count == 0 || keep.Count == n)
            return;

        _rootMoves.Clear();
        for (int i = 0; i < keep.Count; i++)
            _rootMoves.Add(keep[i]);
    }

    private bool TryRankRootMovesByDtz(Board board, Span<int> ranks,
                                       out int bestRank)
    {
        bestRank = int.MinValue;
        int rule50Count = board.HalfmoveClock;
        bool repeated = board.HasRepeated();
        var replies = new MoveList();

        for (int i = 0; i < _rootMoves.Count; i++)
        {
            board.MakeMove(_rootMoves[i]);
            int dtz;
            bool ok;

            if (board.HalfmoveClock == 0)
            {
                // The root move itself captured, pushed a pawn or promoted: it
                // already is the next zeroing move, whatever the child's DTZ.
                ok = Tablebases.Syzygy.ProbeWdl(board, out var childWdl);
                var rootWdl = (Tablebases.WdlScore)(-(int)childWdl);
                dtz = ok ? Tablebases.Syzygy.DtzBeforeZeroing(rootWdl) : 0;
            }
            else if ((Syzygy50MoveRule && board.HalfmoveClock >= 100)
                     || board.CountRepetitions() >= 1)
            {
                // A reversible root move that immediately reaches a draw must
                // rank as a draw, regardless of the counter-free TB verdict.
                ok = true;
                dtz = 0;
            }
            else
            {
                ok = Tablebases.Syzygy.ProbeDtz(board, out int childDtz);
                dtz = -childDtz;
                dtz += Math.Sign(dtz);            // Include the root ply

                // probe_dtz reports the child mate as -1; after adding the
                // root ply that becomes 2, but the mating move itself is DTZ 1.
                if (ok && dtz == 2 && board.IsInCheck())
                {
                    MoveGenerator.GenerateLegalMoves(board, replies);
                    if (replies.Count == 0)
                        dtz = 1;
                }
            }

            board.UnmakeMove();

            if (!ok)
                return false;

            int rank = RootDtzRank(dtz, rule50Count, repeated);
            ranks[i] = rank;
            if (rank > bestRank)
                bestRank = rank;
        }

        return true;
    }

    // Reference root_probe_wdl fallback. It deliberately keeps cursed wins
    // and blessed losses in distinct bands, so missing .rtbz files cost only
    // DTZ precision rather than the game-theoretic safety of the root choice.
    private bool TryRankRootMovesByWdl(Board board, Span<int> ranks,
                                       out int bestRank)
    {
        bestRank = int.MinValue;

        for (int i = 0; i < _rootMoves.Count; i++)
        {
            board.MakeMove(_rootMoves[i]);
            bool draw = (Syzygy50MoveRule && board.HalfmoveClock >= 100)
                     || board.CountRepetitions() >= 1;
            bool ok;
            Tablebases.WdlScore rootWdl;

            if (draw)
            {
                ok = true;
                rootWdl = Tablebases.WdlScore.Draw;
            }
            else
            {
                ok = Tablebases.Syzygy.ProbeWdl(board, out var childWdl);
                rootWdl = (Tablebases.WdlScore)(-(int)childWdl);
            }

            board.UnmakeMove();

            if (!ok)
                return false;

            // Do not collapse cursed wins or blessed losses to draws here.
            // Reference deliberately gives them the bands immediately above
            // and below a draw, retaining their practical preference while
            // still distinguishing them from unconditional wins and losses.
            int rank = rootWdl switch
            {
                Tablebases.WdlScore.Loss => -MaxDtz,
                Tablebases.WdlScore.BlessedLoss => -MaxDtz + 101,
                Tablebases.WdlScore.Draw => 0,
                Tablebases.WdlScore.CursedWin => MaxDtz - 101,
                Tablebases.WdlScore.Win => MaxDtz,
                _ => throw new InvalidOperationException("Invalid WDL result")
            };

            ranks[i] = rank;
            if (rank > bestRank)
                bestRank = rank;
        }

        return true;
    }

    private static int RootDtzRank(int dtz, int rule50Count, bool repeated)
        => dtz > 0
            ? dtz + rule50Count <= 99 && !repeated
                ? MaxDtz - dtz
                : MaxDtz / 2 - (dtz + rule50Count)
         : dtz < 0
            ? -dtz * 2 + rule50Count < 100
                ? -MaxDtz - dtz              // Longer loss (more negative DTZ) ranks higher
                : -MaxDtz / 2 + (-dtz + rule50Count)
         : 0;

    // 'excluded' is the singular-extension verification mode: that one move is
    // skipped and the node must NOT use its own TT entry (which describes the
    // search WITH the move) nor store its result (it describes a different,
    // move-less position). Move.None means a normal search.
    private int Negamax(Board board, int depth, int alpha, int beta, int ply, bool allowNull,
                        Move excluded = default)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        // Ply overflow guard for recursive and singular-extension searches.
        if (ply >= MaxPly)
            return _evaluator.Evaluate(board);

        bool inCheck = board.IsInCheck();

        // ---- Draws by rule. Checked before the TT: a cached score cannot
        // know the path's repetition count or fifty-move clock. Checkmate has
        // precedence at clock 100, so an in-check node first proves that an
        // escape exists; this path is exceptionally rare and can afford the
        // allocation-free legal-move probe.
        if (board.HalfmoveClock >= 100)
        {
            if (!inCheck || MoveGenerator.HasLegalMove(board, _moveLists[ply]))
                return 0;
            return -MateScore + ply;
        }
        if (board.HalfmoveClock >= 4 && board.CountRepetitions() >= 1)
            return 0;
        if (GameState.IsDeadPosition(board))
            return 0;

        // A reversible move may be about to enter a repeated position even
        // though the current key itself is new. Raising alpha to draw avoids
        // searching for a loss below a cycle the side can force immediately.
        if (alpha < 0 && board.HasUpcomingRepetition(ply))
        {
            alpha = 0;
            if (alpha >= beta)
                return alpha;
        }

        // ---- Transposition table probe ----
        Move ttMove = Move.None;
        bool ttHit = _tt.Probe(board.ZobristKey, out TTEntry entry);
        if (ttHit)
        {
            ttMove = entry.BestMove; // Always useful for ordering.

            // The stored score is only reusable if it comes from a search at
            // least as deep as the one we are about to do, and its bound type
            // allows a conclusion within the current window (None = eval-only
            // entry, no score). Never in singular verification mode: the
            // entry describes the search WITH the excluded move available.
            if (entry.Depth >= depth && excluded == Move.None
                && CanReuseTtScore(entry.Score, board.HalfmoveClock)
                && entry.Bound != BoundType.None)
            {
                int score = FromTT(entry.Score, ply);
                switch (entry.Bound)
                {
                    case BoundType.Exact:
                        return score;
                    case BoundType.LowerBound when score >= beta:
                        return score;
                    case BoundType.UpperBound when score <= alpha:
                        return score;
                }
            }
        }

        // ---- Syzygy tablebase probe ----
        // A hit here is exact knowledge, so the node is finished: no search can
        // improve on it. Only when the fifty-move counter is zero, because the
        // tables answer "won" without regard to that rule and a win that needs
        // more plies than the counter allows is really a draw. Castling rights
        // are refused inside the prober for the same class of reason.
        // Guard ordered by selectivity, not by readability. The piece count is
        // the test that fails at practically every middlegame node, so it goes
        // first and short-circuits the rest; _tbMaxMen is 0 when no tablebases
        // are loaded, which disables the whole block with a single compare.
        // Measured: the previous ordering cost 3.5% NPS on positions that never
        // probe at all, which is pure loss.
        int pieceCount = System.Numerics.BitOperations.PopCount(board.AllOccupancy);
        if (pieceCount <= _tbMaxMen
            && (pieceCount < _tbMaxMen || depth >= _tbMinProbeDepth)
            && board.HalfmoveClock == 0 && ply > 0
            && excluded == Move.None)
        {
            if (Tablebases.Syzygy.ProbeWdl(board, out var wdlScore))
            {
                TbHits++;

                // With the fifty-move rule respected a cursed win is only a
                // draw; analysis that ignores the rule wants the real verdict.
                int wdl = (int)wdlScore;
                if (Syzygy50MoveRule)
                    wdl = wdl switch { 1 => 0, -1 => 0, _ => wdl };

                int tbScore = wdl > 0 ? TbWin - ply
                            : wdl < 0 ? -TbWin + ply
                            : 0;

                BoundType tbBound = wdl > 0 ? BoundType.LowerBound
                                  : wdl < 0 ? BoundType.UpperBound
                                  : BoundType.Exact;

                // Only cut when the bound actually resolves the window; an
                // exact draw always does.
                if (tbBound == BoundType.Exact
                    || (tbBound == BoundType.LowerBound && tbScore >= beta)
                    || (tbBound == BoundType.UpperBound && tbScore <= alpha))
                {
                    _tt.Store(board.ZobristKey, depth + 6, ToTT(tbScore, ply),
                              TTEntry.NoStaticEval, tbBound, Move.None,
                              isPv: beta - alpha != 1 || (ttHit && entry.IsPv));
                    return tbScore;
                }
            }
        }

        // ---- Internal Iterative Reductions ----
        // No TT move at a node that deserves real depth means either the
        // position was never searched or its entry was overwritten — move
        // ordering will be poor and the full depth is not worth its cost.
        // Search one ply shallower; if the node matters, a later (deeper)
        // visit will find a TT move waiting and search it properly.
        if (depth >= 4 && ttMove == Move.None && excluded == Move.None)
            depth--;

        // ---- Horizon: switch to quiescence instead of a raw evaluation ----
        if (depth <= 0)
            return Quiescence(board, alpha, beta, ply);

        // Non-PV nodes are searched with a null window (beta == alpha + 1);
        // the aggressive prunings below only fire there, never on the principal
        // variation where a wrong cut would corrupt the reported line.
        bool nonPv = beta - alpha == 1;

        // "Is or has been on the PV": every PV node, plus any node whose TT
        // entry carries the flag from an earlier visit through the PV.
        // Stored back on every write so the mark survives re-searches.
        bool ttPv = !nonPv || (ttHit && entry.IsPv);

        // Static evaluation, reused by the forward-pruning heuristics. Skipped
        // in check (the position is not "quiet" and the eval is meaningless).
        // A TT hit serves the cached eval instead of running the evaluator
        // (the big 5F speedup: revisits pay one cluster read, not a full
        // evaluation); a miss caches what we compute in an eval-only entry so
        // the NEXT visit — often via IIR or a re-search — skips it too.
        int rawStaticEval;
        int staticEval;
        if (inCheck)
        {
            rawStaticEval = 0;
            staticEval = 0;
        }
        else
        {
            if (ttHit && entry.StaticEval != TTEntry.NoStaticEval)
            {
                rawStaticEval = entry.StaticEval;
            }
            else
            {
                rawStaticEval = _evaluator.Evaluate(board);
                if (excluded == Move.None)
                    _tt.Store(board.ZobristKey, 0, 0, rawStaticEval,
                              BoundType.None, Move.None, ttPv);
            }

            staticEval = _pawnCorrectionHistory.Correct(board, rawStaticEval);
        }

        // ---- Improvement / improving ----
        // How much our static eval gained over our previous position — two
        // plies back, or four when the previous position was a check. Feeds
        // the NMP entry margin as a value and everything else as the boolean
        // improving flag. The cold-start default (+83cp, the reference's 173
        // x0.48) assumes improving: near the root prunings stay conservative.
        // Strict semantics (5A, validated): no eval history means NOT
        // improving. The reference defaults to improving (+173) instead, but
        // that default relaxes LMR and LMP across every cold shallow node and
        // measurably bloats our tree (+36% at depth 15 with it in place).
        _stackEval[ply] = inCheck ? NoEval : staticEval;
        int improvement = inCheck ? 0
            : ply >= 2 && _stackEval[ply - 2] != NoEval ? staticEval - _stackEval[ply - 2]
            : ply >= 4 && _stackEval[ply - 4] != NoEval ? staticEval - _stackEval[ply - 4]
            : 0;
        bool improving = improvement > 0;

        // ---- Reverse futility pruning (a.k.a. static null move) ----
        // If our static eval is so far above beta that even conceding a healthy
        // margin per remaining ply keeps us above it, the opponent will avoid
        // this line — return without searching. Only at shallow depth and away
        // from mate scores, where the static eval is a trustworthy proxy.
        // An improving eval is trending up and can be trusted one depth-step
        // sooner (reference: margin × (depth - improving)); the parent move's
        // statScore leans on the margin — after a well-reputed parent move the
        // cut comes easier, after a maligned one it needs more headroom.
        if (!inCheck && nonPv && depth <= 6 && Math.Abs(beta) < MateBound
            && excluded == Move.None
            && staticEval >= beta
            && staticEval - 85 * (depth - (improving ? 1 : 0))
               - (ply > 0 ? _stackStatScore[ply - 1] : 0) / StatScoreRfpDiv >= beta)
            return staticEval;

        // ---- Null Move Pruning (with verification search) ----
        // "Pass" the turn: if the opponent moving twice in a row still cannot
        // bring us below beta, no real move will either — prune the branch.
        // Reference entry condition: non-PV only, never in check or right
        // after another null (the position must re-anchor between passes),
        // never without non-pawn material (zugzwang), only when the static
        // eval clears beta by a margin that shrinks with depth and improvement
        // and grows with complexity, and not while the parent move's statScore
        // says the parent is already refuting everything cheaply. During a
        // verification search the verifying side cannot null again below
        // nmpMinPly (a false null cutoff must not verify itself).
        // Entry: the previously validated shape (any node at depth >= 3, no
        // eval precondition — a cheap probe everywhere). The reference gates
        // entry on staticEval >= beta plus a depth/improvement/complexity
        // margin and a statScore filter; measured here, that gating grows the
        // tree ~30% at equal tactics because our classical eval is noisy
        // relative to the search — probes at eval-below-beta nodes keep
        // finding real cutoffs the gate would forbid. Revisit with NNUE.
        if (allowNull && !inCheck && depth >= 3 && ply > 0 && excluded == Move.None
            && board.HasNonPawnMaterial(board.SideToMove)
            && (ply >= _nmpMinPly || board.SideToMove != _nmpColor))
        {
            // Reduction: the previously validated shape (child depth
            // depth - 3 - depth/4). The reference's deeper dynamic R
            // (min((eval-beta)/168, 7) + depth/3 + 4 - (complexity > 861))
            // is DEFERRED to 5C+: its null probes bottom out in quiescence
            // across depths 3-7, and OUR quiescence is captures-only — the
            // reference's generates CHECKS at the first qs ply, which is what
            // keeps its shallow null cutoffs tactically safe (measured here:
            // WAC 249-251/300 vs 257-259 with the old R, and verification
            // onset at 8 neither recovers the tactics nor keeps the nodes).
            int r = 3 + depth / 4;

            _stackPiece[ply] = -1; // No usable "previous move" for the child.
            _stackStatScore[ply] = 0;
            _incremental?.PushNull();
            board.MakeNullMove();
            int nullScore = -Negamax(board, depth - r, -beta, -beta + 1,
                                     ply + 1, allowNull: false);
            board.UnmakeNullMove();

            if (_stopped)
                return 0;

            if (nullScore >= beta && nullScore < MateBound)
            {
                // Mate-range null scores never cut (the guard above): a mate
                // "found" after passing a move is exactly the unproven kind —
                // falling through to the real search keeps forced mates visible
                // at the depth they deserve (measured: the reference's cap-to-
                // beta hid a WAC mate-in-4 through depth 17 on our search).

                // Shallow nodes trust the null cutoff outright; so does any
                // node inside a verification search (no recursive verifying).
                if (_nmpMinPly > 0 || (Math.Abs(beta) < MateBound && depth < 14))
                    return nullScore;

                // High depth: verify with a real reduced search on the SAME
                // position, null moves disabled for us until past nmpMinPly.
                _nmpMinPly = ply + 3 * (depth - r) / 4;
                _nmpColor = board.SideToMove;
                int v = Negamax(board, depth - r, beta - 1, beta, ply, allowNull: false);
                _nmpMinPly = 0;

                if (_stopped)
                    return 0;
                if (v >= beta)
                    return nullScore;
            }
        }

        // ---- ProbCut ----
        // A promising capture may prune this node only after passing both a
        // qsearch filter and a regular reduced search. The depth floor is the
        // critical correction: no cutoff may rest on qsearch alone.
        int probBeta = beta + ProbCutMargin
                     - ProbCutImprovingMargin * (improving ? 1 : 0);
        if (!inCheck && depth >= 3 && excluded == Move.None
            && Math.Abs(beta) < MateBound
            && !(ttHit && entry.Bound != BoundType.None
                 && FromTT(entry.Score, ply) < probBeta))
        {
            int probCutDepth = Math.Max(depth - (improving ? 5 : 3), 1);
            MoveList captures = _moveLists[ply];
            MoveGenerator.GeneratePseudoLegalMoves(board, captures, capturesOnly: true);
            MovePicker.OrderCaptures(captures, board, _captureHistory);
            Color mover = board.SideToMove;

            for (int i = 0; i < captures.Count; i++)
            {
                Move move = captures[i];

                if (move.IsPromotion && move.Flag is not (MoveFlag.PromoQueen or MoveFlag.PromoQueenCapture))
                    continue;

                // The exchange must be capable of bridging the gap between the
                // static evaluation and the deliberately higher ProbCut bar.
                // The simplified SEE intentionally cannot model the material
                // gain of promotion. Queen promotions are therefore always
                // admitted, as they were before the gap-based SEE gate.
                if (!PassesProbCutSeeGate(board, move, probBeta - staticEval))
                    continue;

                _incremental?.PushMove(board, move);
                board.MakeMove(move);
                if (board.IsSquareAttacked(board.KingSquare(mover), board.SideToMove))
                {
                    board.UnmakeMove();
                    _incremental?.Pop();
                    continue;
                }
                _stackPiece[ply] = ContinuationHistory.PieceIndex(mover, board.PieceTypeAt(move.To));
                _stackTo[ply] = move.To;
                _stackStatScore[ply] = 0;

                int score = -Quiescence(board, -probBeta, -probBeta + 1, ply + 1);
                if (score >= probBeta)
                    score = -Negamax(board, probCutDepth, -probBeta, -probBeta + 1,
                                     ply + 1, allowNull: false);

                board.UnmakeMove();
                _incremental?.Pop();

                if (_stopped)
                    return 0;
                if (score >= probBeta)
                {
                    _tt.Store(board.ZobristKey, probCutDepth + 1, ToTT(score, ply),
                              rawStaticEval, BoundType.LowerBound, move, ttPv);

                    // Reduced searches do not establish mate/TB scores.
                    if (Math.Abs(score) < MateBound)
                        return score - (probBeta - beta);
                }
            }
        }

        // A sufficiently deep TT lower bound far above beta can provide the
        // same evidence without repeating the capture probe.
        int smallProbBeta = beta + SmallProbCutMargin;
        if (!inCheck && excluded == Move.None && ttHit
            && entry.Bound == BoundType.LowerBound
            && entry.Depth >= depth - 4 && Math.Abs(beta) < MateBound)
        {
            int ttScore = FromTT(entry.Score, ply);
            if (ttScore >= smallProbBeta && Math.Abs(ttScore) < MateBound)
                return smallProbBeta;
        }
        // ---- Singular extension detection ----
        // A TT move whose stored score is trustworthy gets a verification
        // search: all OTHER moves are searched shallower against a lowered
        // window. If none comes close, the TT move is "singular" — the only
        // move holding the position — and deserves an extra ply, because
        // getting forced lines right is what wins/saves games.
        int singularExtension = 0;
        if (depth >= 8 && excluded == Move.None && ttMove != Move.None
            && ttHit && entry.Depth >= depth - 3 && entry.Bound != BoundType.UpperBound
            && CanReuseTtScore(entry.Score, board.HalfmoveClock))
        {
            int ttScore = FromTT(entry.Score, ply);
            if (Math.Abs(ttScore) < MateBound)
            {
                int singularBeta = ttScore - 2 * depth;
                int score = Negamax(board, (depth - 1) / 2, singularBeta - 1, singularBeta,
                                    ply, allowNull: false, excluded: ttMove);
                if (_stopped)
                    return 0;
                if (score < singularBeta)
                    singularExtension = 1;
            }
        }

        // ---- Staged move picking ----
        // Legality is checked lazily at make time (like quiescence does), and
        // generation itself is staged so a node that cuts off early never pays
        // for moves it does not reach:
        //   stage 0: the TT move alone, vetted by IsPseudoLegal — no generation.
        //   stage 1: captures/promotions, sorted; served while SEE-good.
        //   stage 2: quiet moves (sorted with any unserved losing captures,
        //            which sink to the very end by score band).
        // The order served is identical to the old full-sort ordering.
        MoveList moves = _moveLists[ply];
        moves.Clear();
        bool ttServed = ttMove != Move.None && MoveGenerator.IsPseudoLegal(board, ttMove);
        if (ttServed)
            moves.Add(ttMove);

        // Previous-move context for counter moves and continuation history
        // (absent at the root or right after a null move).
        int prevPiece = ply > 0 ? _stackPiece[ply - 1] : -1;
        int prevTo = prevPiece >= 0 ? _stackTo[ply - 1] : 0;
        Move counterMove = prevPiece >= 0 ? _counterMoves[prevPiece, prevTo] : Move.None;

        Color stm = board.SideToMove;
        int originalAlpha = alpha;
        Move bestMove = Move.None;
        int bestScore = -Infinity;
        int searched = 0;
        int quietsSearched = 0;
        int stage = 0; // 0 = only TT move in the list, 1 = captures appended, 2 = quiets appended

        // Quiet moves actually searched at this node, kept so that a later
        // beta cutoff can punish them (history malus): they had their chance
        // before the cutoff move and did not refute.
        Span<Move> triedCaptures = stackalloc Move[48];
        int triedCaptureCount = 0;
        Span<Move> triedQuiets = stackalloc Move[64];
        int triedQuietCount = 0;

        for (int i = 0; ; i++)
        {
            // Stage transitions: generate the next batch when the list runs
            // out, or when serving is about to reach a losing capture (those
            // must wait until after the quiets). Loops because a stage can
            // come up empty (no captures / no quiets).
            bool exhausted = false;
            while (i == moves.Count || (stage == 1 && moves.Scores[i] < 0))
            {
                if (stage == 0)
                {
                    stage = 1;
                    MoveGenerator.AppendCaptureMoves(board, moves);
                    MovePicker.ScoreAndSortCaptures(moves, i, board, _captureHistory);
                }
                else if (stage == 1)
                {
                    stage = 2;
                    int quietsFrom = moves.Count;
                    MoveGenerator.AppendQuietMoves(board, moves);
                    MovePicker.ScoreAndSortQuiets(moves, quietsFrom, sortFrom: i, board,
                        _killers, _history, ply, _contHist, prevPiece, prevTo, counterMove,
                        depth);
                }
                else
                {
                    exhausted = true;
                    break;
                }
            }
            if (exhausted)
                break;

            Move move = moves[i];
            if (ttServed && i > 0 && move == ttMove)
                continue; // The generators re-emit the TT move; already served.
            if (move == excluded)
                continue; // Singular verification searches everything BUT this.
            bool isQuiet = !move.IsCapture && !move.IsPromotion;

            // The move's combined history signal (2x butterfly + continuation
            // history) and the depth it would actually receive after LMR:
            // the shallow-pruning margins below scale with the REDUCED depth
            // (reference lmrDepth), not the nominal one — a move that will be
            // probed shallow anyway is pruned against that shallower horizon.
            int movePieceIdx = ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(move.From));
            int moveHistory = 2 * _history.Get(stm, move)
                + (prevPiece >= 0 ? _contHist.Get(prevPiece, prevTo, movePieceIdx, move.To) : 0);
            int lmrDepth = depth - 1;
            if (searched > 0)
            {
                // In 1024ths like the reduction proper, truncated once here.
                int rEst = LmrReductions[Math.Min(depth, 63), Math.Min(searched, 63)];
                if (nonPv) rEst += LmrScale;
                if (!improving) rEst += LmrScale;
                lmrDepth = Math.Max(depth - 1 - rEst / LmrScale, 0);
            }

            // ---- Forward pruning of quiet moves (shallow, non-PV, not in
            //      check, at least one move already searched so a best move is
            //      guaranteed) ----
            if (isQuiet && searched > 0 && nonPv && !inCheck && Math.Abs(alpha) < MateBound)
            {
                // Late move pruning: once enough quiet moves have been tried at
                // low depth, the remaining ones are very unlikely to be best.
                // In a worsening position quiet moves rarely save the node —
                // halve the count before the cut (reference LMP shape).
                int lmpThreshold = 3 + depth * depth;
                if (!improving) lmpThreshold /= 2;
                if (depth <= 3 && quietsSearched >= lmpThreshold)
                    continue;

                // Futility pruning (reference parent-node shape): if the static
                // eval plus a margin that grows with the LMR-reduced depth
                // cannot reach alpha, the quiet move will not rescue the node.
                // The move's own history buys a reprieve — a move the tables
                // like must not be pruned on eval alone (values 106/145 at
                // their x0.48 equivalents, history divisor unit-rescaled).
                // Futility pruning: if even a generous per-ply margin over the
                // static eval cannot lift it to alpha, a quiet move will not
                // rescue the node — skip it. The reference's lmrDepth-scaled
                // reshape (106 + 145*lmrDepth up to lmrDepth 13) is DEFERRED
                // to 5C: it presupposes the reference's larger LMR reductions
                // (which keep its lmrDepth systematically lower) — measured
                // here, both the x0.48 and the raw margins made forced mates
                // invisible (WAC.001 mate-in-4: found at d13 before, hidden
                // past d17 / 100M nodes with the reshape in either scale).
                if (depth <= 4 && staticEval + 100 * depth <= alpha)
                    continue;
            }

            // ---- Shallow capture pruning (non-PV, not in check) ----
            if (move.IsCapture && !move.IsPromotion && searched > 0 && !inCheck)
            {
                // SEE pruning near the horizon: a capture that clearly loses
                // material will not recover the loss in the couple of plies
                // left; skip it.
                if (depth <= 2
                    && StaticExchangeEvaluator.LosesAtLeast(board, move, threshold: 100))
                    continue;
            }

            // The singular extension applies to the TT move only: it is the
            // move whose uniqueness the verification search just proved.
            int newDepth = depth - 1 + (move == ttMove ? singularExtension : 0);

            _stackPiece[ply] = movePieceIdx;
            _stackTo[ply] = move.To;
            _stackStatScore[ply] = moveHistory - StatScoreOffset;
            _incremental?.PushMove(board, move);
            board.MakeMove(move);

            // Lazy legality: a pseudo-legal move that leaves our king attacked
            // is discarded here, at the only make it will ever get.
            if (board.IsSquareAttacked(board.KingSquare(stm), board.SideToMove))
            {
                board.UnmakeMove();
                _incremental?.Pop();
                continue;
            }

            int score;

            if (searched == 0)
            {
                // PVS: the first (best-ordered) move gets the full window.
                score = -Negamax(board, newDepth, -beta, -alpha, ply + 1, allowNull: true);
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
                    // Everything below is in 1024ths. Every adjuster here is a
                    // whole number of plies, so the single truncation at the
                    // end reproduces the previous per-term integer arithmetic
                    // exactly: floor(a) + k == floor(a + k) for integer k.
                    int r = LmrReductions[Math.Min(depth, 63), Math.Min(searched, 63)];
                    if (nonPv) r += LmrScale;            // Reduce harder off the PV.

                    // History-informed adjustment: a quiet move the history
                    // tables like is reduced less (it keeps refuting things
                    // elsewhere); a disliked one is reduced more. Killers and
                    // the counter move also earn a shallower reduction.
                    r -= Math.Clamp(_history.Get(stm, move) / 16384, -2, 2) * LmrScale;
                    if (move == counterMove || _killers.Rank(ply, move) > 0)
                        r -= LmrScale;

                    // Position is worsening: the remaining moves are even less
                    // likely to be good — reduce them one extra ply.
                    if (!improving) r += LmrScale;

                    reduction = r / LmrScale;
                    if (reduction < 0) reduction = 0;
                    if (reduction > newDepth - 1) reduction = newDepth - 1;
                }

                // PVS null window (cheap refutation attempt), possibly reduced.
                score = -Negamax(board, newDepth - reduction, -alpha - 1, -alpha,
                                 ply + 1, allowNull: true);

                // The reduced probe beat alpha: verify at full depth first.
                if (score > alpha && reduction > 0 && !_stopped)
                    score = -Negamax(board, newDepth, -alpha - 1, -alpha,
                                     ply + 1, allowNull: true);

                // Still inside the window: it is a genuine PV candidate,
                // re-search with the real window.
                if (score > alpha && score < beta && !_stopped)
                    score = -Negamax(board, newDepth, -beta, -alpha,
                                     ply + 1, allowNull: true);
            }

            board.UnmakeMove();
            _incremental?.Pop();
            searched++;
            if (isQuiet)
            {
                quietsSearched++;
                if (triedQuietCount < triedQuiets.Length)
                    triedQuiets[triedQuietCount++] = move;
            }
            else if (move.IsCapture && triedCaptureCount < triedCaptures.Length)
            {
                triedCaptures[triedCaptureCount++] = move;
            }

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
                        // ordering heuristics feed on. The cutoff move gets a
                        // bonus everywhere (killers, counter move, butterfly
                        // and continuation history); the quiets tried before
                        // it get a malus — they had their chance and failed.
                        if (isQuiet)
                        {
                            _killers.Store(ply, move);
                            _history.AddBonus(stm, move, depth);

                            int piece = ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(move.From));
                            if (prevPiece >= 0)
                            {
                                _counterMoves[prevPiece, prevTo] = move;
                                _contHist.AddBonus(prevPiece, prevTo, piece, move.To, depth);
                            }

                            for (int q = 0; q < triedQuietCount; q++)
                            {
                                Move tried = triedQuiets[q];
                                if (tried == move)
                                    continue;
                                _history.AddMalus(stm, tried, depth);
                                if (prevPiece >= 0)
                                    _contHist.AddMalus(prevPiece, prevTo,
                                        ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(tried.From)),
                                        tried.To, depth);
                            }
                        }
                        else if (move.IsCapture)
                        {
                            // A capture produced the cutoff: it earns capture
                            // history, which is what the quiescence capture
                            // ordering reads. The board is restored here, so
                            // the victim is back on its square.
                            _captureHistory.AddBonus(
                                ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(move.From)),
                                move.To, CaptureHistory.VictimIndex(board, move), depth * depth);
                        }

                        // Captures tried before the cutoff move failed to
                        // produce it and sink in the ordering next time, no
                        // matter what kind of move actually cut (reference).
                        for (int c = 0; c < triedCaptureCount; c++)
                        {
                            Move tried = triedCaptures[c];
                            if (tried == move)
                                continue;
                            _captureHistory.AddMalus(
                                ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(tried.From)),
                                tried.To, CaptureHistory.VictimIndex(board, tried), depth * depth);
                        }
                        break;
                    }
                }
            }
        }

        // No legal move was made: mate or stalemate. The ply is added to the
        // mate score so the engine prefers the SHORTEST mate and, when mated,
        // drags it out as long as possible. In singular verification mode the
        // excluded TT move is the only legal move — report a fail-low so the
        // caller marks it singular (never a mate score: the move exists).
        if (searched == 0)
            return excluded != Move.None ? alpha
                 : inCheck ? -MateScore + ply : 0;

        // Every move may have been SEE-pruned except the first; bestMove is
        // then still valid (the first move is never pruned).

        // ---- Store the result in the TT with the right bound type ----
        // Not in singular verification mode: the searched position (one move
        // forbidden) is not the position the key describes.
        if (excluded == Move.None)
        {
            BoundType bound = bestScore <= originalAlpha ? BoundType.UpperBound
                            : bestScore >= beta ? BoundType.LowerBound
                            : BoundType.Exact;
            _tt.Store(board.ZobristKey, depth, ToTT(bestScore, ply),
                      inCheck ? TTEntry.NoStaticEval : rawStaticEval, bound, bestMove, ttPv);

            // Learn only from quiet conclusions whose bound points in the same
            // direction as the evaluation error. Captures/promotions change the
            // material picture too abruptly to teach a pawn-structure bias.
            bool quietBest = bestMove != Move.None && !bestMove.IsCapture && !bestMove.IsPromotion;
            bool boundAgrees = bestScore >= beta ? bestScore > staticEval
                              : bestScore <= originalAlpha ? bestScore < staticEval
                              : true;
            if (!inCheck && quietBest && boundAgrees && Math.Abs(bestScore) < TbScoreBound)
                _pawnCorrectionHistory.Update(board, bestScore - staticEval, depth);
        }

        return bestScore;
    }

    // Promotions are deliberately exempt: the current SEE does not model the
    // promoted piece and reports only the captured victim (or one pawn for a
    // quiet promotion), grossly understating their material gain.
    private static bool PassesProbCutSeeGate(Board board, Move move, int threshold)
        => move.IsPromotion || StaticExchangeEvaluator.Evaluate(board, move) >= threshold;

    // Quiescence search: at the horizon, keep searching forcing moves until the
    // position is quiet, then evaluate. This removes the horizon effect: a
    // depth-limited search would otherwise happily evaluate a position right
    // after QxP, never seeing the recapture ...RxQ one ply beyond its horizon.
    //
    // IN CHECK the node follows a completely different path, matching the
    // reference. This is CORRECTNESS, not a strength tweak: the previous
    // captures-only version got all four parts wrong, and every capture that
    // gives check lands the opponent in exactly this node — so the hole sat on
    // the main line of every tactical sequence, and every caller that verifies
    // a capture through quiescence (ProbCut, null-move probes, multi-cut) was
    // reading those wrong scores as proof.
    //
    //   * No stand-pat. The static eval of a position whose king is attacked
    //     is meaningless, and the side to move is NOT free to "do nothing", so
    //     the premise of the stand-pat floor fails outright. The old code
    //     stood pat anyway and could return a beta cutoff while being mated.
    //   * ALL moves, not just captures. The only escape from a check is often
    //     a quiet king step or an interposition; searching captures alone made
    //     those escapes literally invisible.
    //   * No pruning at all. The reference expresses this by starting bestValue
    //     at -infinity, which makes its whole pruning block unreachable while
    //     in check; here the guards are explicit for clarity.
    //   * Mate detection. In check with no legal reply it is checkmate; the old
    //     code returned the stand-pat score as if nothing had happened.
    //
    // Scores are fail-soft (the real bestScore, never the alpha/beta rail), so
    // callers receive the tightest bound this node actually established.
    //
    // NOT ported here, deliberately: the reference's tuned quiescence constants
    // — stand-pat beta softening (441/583 and 462/562 in 1024ths), futilityBase
    // = staticEval + 306, the moveCount > 2 cut, and its SEE >= -74 threshold
    // (ours prunes at SEE >= 0). Those are heuristic constants and the project
    // rule is that they do not transfer without their ecosystem; they get their
    // own measured block. The TT probe/store at quiescence depth is also left
    // out: measured in the 5E campaign, depth-0 entries flooded the clusters
    // and evicted main-search entries (d15 nodes ROSE 1.35M -> 1.75M, nps -11%).
    private int Quiescence(Board board, int alpha, int beta, int ply)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        bool inCheck = board.IsInCheck();

        // Ply ceiling, checked before the per-ply move list is indexed.
        if (ply >= MaxPly)
            return inCheck ? 0 : _evaluator.Evaluate(board);

        // Quiet check evasions can reach clock 100 or complete a repetition,
        // so every qsearch node must enforce the rules, not only checked ones.
        // As in the main search, a mate on the 100th halfmove wins before the
        // draw claim and is verified by the rare legal-move probe.
        if (board.HalfmoveClock >= 100)
        {
            if (!inCheck || MoveGenerator.HasLegalMove(board, _moveLists[ply]))
                return 0;
            return -MateScore + ply;
        }
        if (board.HalfmoveClock >= 4 && board.CountRepetitions() >= 1)
            return 0;
        if (GameState.IsDeadPosition(board))
            return 0;

        if (alpha < 0 && board.HasUpcomingRepetition(ply))
        {
            alpha = 0;
            if (alpha >= beta)
                return alpha;
        }

        int bestScore;
        int futilityBase;

        if (inCheck)
        {
            // No stand-pat floor: every evasion must be searched, and this
            // sentinel is also what makes "no move improved it" mean mate.
            // The reference relies on exactly this value to make its whole
            // pruning block unreachable while in check; here the pruning is
            // additionally guarded explicitly.
            bestScore = -Infinity;
            futilityBase = -Infinity;
        }
        else
        {
            // "Stand pat": the side to move is never forced to capture, so the
            // static evaluation is a floor for its score. If even doing nothing
            // beats beta, the opponent will avoid this line — cut immediately.
            int rawEval = _evaluator.Evaluate(board);
            bestScore = _pawnCorrectionHistory.Correct(board, rawEval);
            if (bestScore >= beta)
                return bestScore;
            if (bestScore > alpha)
                alpha = bestScore;
            futilityBase = bestScore + QsFutilityMargin;
        }

        // In check: every legal reply is a candidate escape. Otherwise captures
        // and promotions only, which is what keeps quiescence finite.
        MoveList moves = _moveLists[ply];
        MoveGenerator.GeneratePseudoLegalMoves(board, moves, capturesOnly: !inCheck);
        if (inCheck)
            MovePicker.Order(moves, board, Move.None, _killers, _history, ply,
                contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None,
                captureHistory: _captureHistory);
        else
            MovePicker.ScoreAndSortCapturesQs(moves, board, _captureHistory);

        Color us = board.SideToMove;
        int moveCount = 0;

        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];

            // Nothing is pruned while in check: any of these may be the only
            // legal move, and pruning it could turn a save or a draw into a
            // reported mate.
            // Pruning, reference Step 6. Entirely skipped while in check: any
            // of these may be the only legal move, and pruning it could turn a
            // save or a draw into a reported mate. Promotions are exempt too —
            // the piece changes mid-sequence, which the swap algorithm cannot
            // model, and an underpromotion is sometimes the only move that
            // avoids stalemate, delivers mate or dodges a fork. All four
            // promotion pieces are searched (the reference does not drop the
            // minors either); the ordering ranks the queen first, so the
            // others only cost the tail of the list.
            if (!inCheck && !move.IsPromotion)
            {
                // Futility: even winning the piece standing on the destination
                // square, plus a generous margin, cannot reach alpha — the
                // capture is pointless. bestScore is raised to the futility
                // value so the fail-soft bound stays honest (reference).
                int futilityValue = futilityBase + PieceValueQs[(int)(move.Flag == MoveFlag.EnPassant
                    ? PieceType.Pawn : board.PieceTypeAt(move.To))];
                if (futilityValue <= alpha)
                {
                    if (futilityValue > bestScore)
                        bestScore = futilityValue;
                    continue;
                }

                // Even the margin itself cannot reach alpha and the exchange
                // does not bridge the gap on material: skip, again keeping the
                // bound honest (reference: min(alpha, futilityBase)).
                if (futilityBase <= alpha
                    && StaticExchangeEvaluator.Evaluate(board, move) <= 0)
                {
                    int bound = Math.Min(alpha, futilityBase);
                    if (bound > bestScore)
                        bestScore = bound;
                    continue;
                }

                // Deep-losing captures never pay off at the horizon. The
                // reference allows down to -74 in ITS units, where a pawn is
                // 208; ours is 100, so the same threshold is -36. This is
                // looser than the old SEE >= 0 rule on purpose: a slightly
                // losing capture can still be the move that resolves a
                // tactic, and ProbCut/NMP verify their captures through here.
                if (move.IsCapture
                    && StaticExchangeEvaluator.LosesAtLeast(board, move, threshold: QsSeeThreshold))
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

            moveCount++;
            int score = -Quiescence(board, -beta, -alpha, ply + 1);
            board.UnmakeMove();
            _incremental?.Pop();

            if (_stopped)
                return 0;

            if (score > bestScore)
            {
                bestScore = score;
                if (score > alpha)
                {
                    if (score >= beta)
                        break; // Fail high; fail-soft returns bestScore below.
                    alpha = score;
                }
            }
        }

        if (moveCount == 0)
        {
            // In check with no legal reply: checkmate. The ply is added so the
            // engine prefers the SHORTEST mate and, when mated, drags out the
            // longest defense — the same convention the main search uses.
            if (inCheck)
                return -MateScore + ply;

            // Exact stalemate. The old king-and-pawns shortcut missed legal
            // stalemates with a pinned minor. HasLegalMove generates into the
            // already-owned ply buffer and stops at the first legal quiet, so
            // correctness does not require allocating or filtering a full list.
            if (!MoveGenerator.HasLegalMove(board, moves))
                return 0;
        }

        return bestScore;
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
            || ElapsedMs >= _hardTimeMs
            || _nodes >= _maxNodes)
        {
            _stopped = true;
        }
    }

    // Mate and TB scores encode distance from the ROOT. Stored in the TT they
    // must be relative to the NODE, because the same position can be reached
    // at a different ply from another root.
    private static int ToTT(int score, int ply)
    {
        if (score >= TbScoreBound) return score + ply;
        if (score <= -TbScoreBound) return score - ply;
        return score;
    }

    private static int FromTT(int score, int ply)
    {
        if (score >= TbScoreBound) return score - ply;
        if (score <= -TbScoreBound) return score + ply;
        return score;
    }

    // Noa's Zobrist key deliberately omits the halfmove clock. A decisive TT
    // score learned immediately after a zeroing move is therefore unsafe in
    // the same placement with a live rule-50 counter. Keep its move for
    // ordering, but conservatively refuse its bound until the counter resets.
    private static bool CanReuseTtScore(int score, int halfmoveClock)
        => halfmoveClock == 0 || (score > -TbScoreBound && score < TbScoreBound);

}
