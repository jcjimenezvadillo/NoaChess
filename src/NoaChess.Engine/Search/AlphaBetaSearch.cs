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
//   (alpha, alpha+1) - the cheapest possible way to prove "this move is NOT
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
public sealed class AlphaBetaSearch
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

    // Set from the UCI SyzygyProbeLimit / SyzygyProbeDepth options.
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

    // Time-manager diagnostic, enabled with NOA_TM_DEBUG=1. Off by default and
    // read once, so a released build pays a single boolean test per iteration.
    private static readonly bool TimeDebug =
        Environment.GetEnvironmentVariable("NOA_TM_DEBUG") == "1";

    // SETTLED 2026-08-02 with NOA_TM_DEBUG, and it settled the opposite way to
    // the first guess. The scheduler is NOT under-spending: traced across
    // consecutive middlegame moves at 3+1 it spends 2591-13052 ms against an
    // optimum near 5500, averaging about 107% of target, and the dynamic
    // factors swing the budget by 5x between a quiet move (fe 0.500) and a
    // dangerous one (fe 1.500, instability 2.033) exactly as intended.
    //
    // The earlier "it spends half its target" reading came from measuring
    // moves 1-20 only, which is the opening damp doing its job by design. Do
    // not draw conclusions about the time manager from opening plies.
    //
    // Where the clock actually goes unused in real games, in order of size:
    // the bot's own two overhead settings (uci_options.MoveOverhead is
    // reserved x52, so 600 instead of 30 costs 25% of the bullet budget -
    // measured optimum 3189 -> 2345 ms; and lichess-bot subtracts its own
    // move_overhead from the clock it reports), the easy-move rule spending
    // 12% once the score passes 700 cp, and games simply ending before the
    // clock does. The first two are configuration, not engine.

    // How far past the optimum the dynamic factors may extend the soft budget
    // under Lazy SMP. Single-thread searches are not capped here at all; they
    // are already bounded by the hard maximum. See the clamp in FindBestMove.
    private const double SmpExtensionCap = 2.0;

    // Soft budget for a root the tablebases have already decided. The move is
    // guaranteed optimal by the root filter before the search starts, so the
    // search is only breaking ties among winning moves and does not need the
    // whole clock to do it. The hard deadline gets four times this.
    private const long TbResolvedBudgetMs = 300;

    // True once the root move list was successfully ranked from the tablebases,
    // whatever the verdict. Every surviving move is then optimal, so the clock
    // budget can be cut right down - a dead draw deserves the saving as much as
    // a won position does.
    private bool _rootTbResolved;

    // True once the root position itself was resolved by the tablebases and the
    // root move list was ranked by DTZ. It disables tablebase probing INSIDE the
    // search, which is what lets the search choose between moves the tables call
    // equal. See the probe guard in Negamax for the full reasoning.
    private bool _rootInTb;

    // Recomputed after the tablebases are (re)loaded or the limit changes.
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

    // Number of positions this search resolved from tablebases.
    public long TbHits { get; private set; }

    private const int MaxPly = 128;

    // Tunable search parameters (aspiration window width, LMR triggers...).
    // Selected via the UCI "Profile" option; see EngineProfile.
    public Profiles.EngineProfile Profile { get; set; } = Profiles.EngineProfile.Default;

    // How often (in nodes) the time/cancellation check runs. A power-of-two
    // mask makes the check nearly free.
    private const int StopCheckInterval = 2048;

    // "Easy move" time control (clock mode). A decisively winning or losing
    // score that has held for several iterations is not going to change, so the
    // engine plays it on a small fraction of the optimum instead of burning the
    // whole budget. Without this it spent 8.5 s at 5+5 on an obvious recapture
    // whose forced mate it had already seen - bleeding the clock while an
    // instant-moving opponent banked time. Tunable by SPRT.
    private const int EasyMoveMargin = 700;       // |score| (cp) that counts as decisive
    private const int EasyMoveMinDepth = 12;      // do not trust it before this depth
    private const int EasyMoveStableDepth = 6;    // best move unchanged for this many iterations
    private const double EasyMoveFraction = 0.12; // spend at most this share of the optimum

    // Proven-short-mate stop (clock mode). Once a completed iteration proves a
    // forced mate in <= 3 plies for us - which cannot get materially shorter -
    // or that we are mated in <= 2 (no longer defense exists), searching on is
    // pointless: stop and play it. This is the NARROW exception to the "never
    // break on mate scores" rule below: LONG mates still deepen (finding
    // shorter mates / longer defenses), only the shortest proven ones stop
    // early. Mirrors the reference time-manager (search.cpp: score >=
    // mate_in(3) || score == mated_in(2)). Fixes multi-second thinks on an
    // already-seen mate-in-1: the easy-move gate needs depth >= 12, but a
    // mate-in-1 is proven at depth 1-2. Measured at 5+5: 1074 ms -> 22 ms at
    // one thread, 1253 ms -> 112 ms at 30. Clock mode only -> fixed-depth is
    // byte-identical. Set EnableProvenMateStop false for the isolated
    // no-mate-stop candidate build.
    private const bool EnableProvenMateStop = true;
    private const int MateStopGivePlies = 3; // reference mate_in(3): we give mate in <= 3 plies
    private const int MateStopGetPlies = 2;  // reference mated_in(2): we are mated in <= 2 plies

    // Mid-iteration overshoot guard: bound how far a single deep root move may
    // run past the (dynamic) soft budget before the node-level stop fires. The
    // soft deadline is only enforced at root-move boundaries, so a deep
    // iteration whose first root move takes seconds (a won/decisive position
    // reached over a warm TT) otherwise runs to the loose hard maximum. Applies
    // to single-thread AND SMP. See the _maxTimeMs update in FindBestMove.
    private const double OvershootFactor = 1.5;

    private IPositionEvaluator _evaluator;

    // Set when the evaluator keeps incremental state (NNUE accumulators):
    // the search notifies it around every make/unmake. Null for stateless
    // evaluators (classical) - one branch-predicted null check per node.
    private IIncrementalEvaluator? _incremental;

    // The transposition table. A standalone search owns a fresh 64 MB table; a
    // Lazy SMP helper worker instead SHARES the main worker's table (that shared
    // memory is the whole point - threads cross-pollinate through it).
    private readonly TranspositionTable _tt;
    private readonly KillerTable _killers = new(MaxPly);
    private readonly HistoryTable _history = new();
    // Continuation history, one INDEPENDENT table per ply distance: index 0 is
    // keyed on the move one ply back, index 1 on the move two plies back.
    //
    // Independence is the whole point. A single table shared across distances
    // has both keys writing the same entries, which cost -26 Elo when measured
    // in 5G - "the reply to what was just played" and "the reply to what was
    // played two plies ago" are different distributions. Each table is ~2.3 MB,
    // and Lazy SMP gives every worker its own set, so the level count is not
    // free in cache terms either; this is why it starts at two rather than the
    // reference's five.
    //
    // The ORDERING read sums every active level with equal weight. The
    // statScore consumer used by pruning deliberately keeps reading level 0
    // only, so this change cannot move the pruning thresholds as a side effect.
    private readonly ContinuationHistory[] _contHist = [new ContinuationHistory()];
    private readonly CaptureHistory _captureHistory = new();
    private readonly CorrectionHistorySet _corrections = new();

    // Key for the continuation correction table: the (piece, destination) of
    // the move that reached this node, or 0 at the root and after a null move
    // where no such move exists. Kept next to the call sites so the update and
    // the lookup can never end up keyed differently - filing an observation
    // under one key and reading it back under another is silent and permanent.
    private ulong ContinuationCorrectionKey(int ply)
        => ply > 0 && _stackPiece[ply - 1] >= 0
            ? CorrectionHistorySet.ContinuationKey(_stackPiece[ply - 1], _stackTo[ply - 1])
            : 0;
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
    // FLAT, not Move[12, 64]: read once per node and written on every quiet
    // cutoff. Layout: piece * 64 + to.
    private readonly Move[] _counterMoves = new Move[12 * 64];

    // Moves already tried at a node, kept so a beta cutoff can apply the
    // history malus to the ones that failed to produce it. Preallocated per ply
    // instead of stackalloc'd per node: a stackalloc is zero-initialised on
    // every call, and this is 224 bytes of memset at every node for data that
    // is always written before it is read.
    private const int MaxTriedQuiets = 64;
    private const int MaxTriedCaptures = 48;
    private readonly Move[] _triedQuiets = new Move[(MaxPly + 2) * MaxTriedQuiets];
    private readonly Move[] _triedCaptures = new Move[(MaxPly + 2) * MaxTriedCaptures];

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
    // the move's continuation history, minus an offset - in OUR history units.
    // The child consults [ply-1]: a parent move the tables love means the
    // parent line keeps refuting things, so the child skips NMP (the fail-high
    // is already cheap without a null probe) and its RFP margin leans on it.
    private readonly int[] _stackStatScore = new int[MaxPly + 2];

    // statScore-derived thresholds. The reference values are in ITS history
    // units (tables gravity-capped at 14365/29952); ours accumulate depth^2
    // with far smaller magnitudes (measured 2026-07-17: butterfly p99 3218,
    // contHist p99 630 - combined statScore range ~0.28x the reference's), so
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
    // FLAT, not int[64, 64]: read for every reduced move. Layout: depth * 64 + move.
    private static readonly int[] LmrReductions = BuildLmrTable();

    // Reductions are accumulated in 1024ths of a ply and truncated once, at the
    // point of use. The reference keeps its whole reduction pipeline in fixed
    // point for a reason: every one of its adjusters is a FRACTION of a ply.
    // Truncating per term - which an integer table forces - makes each adjuster
    // three to ten times too coarse, and eight of them stack into swings the
    // reference never applies. That granularity is the unnamed "ecosystem" the
    // 5C adjuster suite kept measuring against.
    private const int LmrScale = 1024;

    // NO history-informed LMR adjustment, on measured evidence - the line is
    // closed, not merely unimplemented. Three variants of "let LMR read the
    // butterfly history" were tested against v2.8.3-class baselines and land on
    // a monotone curve by how much reduction they remove:
    //     statScore, continuous, biased to LESS reduction   -18 Elo (H0)
    //     clamp(hist/384, -2, +2), symmetric                -4.8 +/-11.4, LLR -2.89 (H0)
    //     clamp(hist/256, -2,  0), one-sided add-only        +4.2 +/-9.1  flat, 3000 games
    // Every version that clawed moves out of reduction lost, in proportion to how
    // aggressively it did so; the add-only version merely returned to noise. Our
    // base reductions are milder than the reference's, so a history term here is
    // redundant with the killer/counter shallowing already applied and only costs
    // nodes. (For most of the engine's life this line shipped as
    // clamp(hist/16384, -2, 2) and was arithmetically DEAD - the butterfly table
    // is bounded at 7183 so it returned 0 at every node - which is the third
    // inert-threshold bug of that family and is why it was investigated at all.)
    // Do not re-add without a new mechanism; the direct form is settled.


    private static int[] BuildLmrTable()
    {
        var table = new int[64 * 64];
        for (int depth = 1; depth < 64; depth++)
            for (int move = 1; move < 64; move++)
                table[(depth * 64) + move] =
                    (int)((0.75 + Math.Log(depth) * Math.Log(move) / 2.25) * LmrScale);
        return table;
    }

    private long _nodes;
    private long _hardTimeMs;
    // Node-level (mid-iteration) deadline. Equals _hardTimeMs except under Lazy
    // SMP, where it is tightened to a small multiple of the soft budget so a
    // runaway single root move cannot coast to the loose hard maximum (see the
    // update in FindBestMove and the check in CheckStop).
    private long _maxTimeMs;
    private long _softTimeMs;
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

    // Root best-move changes during the current iteration (root fills it,
    // reset each iteration).
    private int _bestMoveChanges;
    // Monotonic best-move-change count for the whole search (reset once per
    // search, never per iteration). The coordinator reads DELTAS of this across
    // main iterations so a helper's changes are counted once, not re-summed
    // every iteration.
    private int _bestMoveChangesTotal;

    // Lazy SMP time-manager coupling. The instability factor must be the AVERAGE
    // root best-move-change rate across ALL workers (reference: totBestMoveChanges
    // / threads.size()), not just this thread's. A single thread's count is noisy
    // and, under the shared-TT races, occasionally spikes the budget to the hard
    // maximum on trivial moves (measured: a forced recapture taking 22s in a 3+2
    // game). Averaging over the pool keeps the factor low-variance. Set by the
    // coordinator on the main worker only; defaults keep the single-thread path
    // byte-identical (count/1 = count, peer func null).
    internal int SearchThreadCount = 1;
    internal Func<int>? PeerBestMoveChanges;
    // Racy read of this worker's monotonic counter for the coordinator's peer
    // delta (benign: a stale int read only nudges a heuristic; int reads atomic).
    internal int BestMoveChangesTotal => _bestMoveChangesTotal;

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

    // Set when the SOFT budget expires at a root-move boundary. Unlike a hard
    // stop, everything searched so far in the iteration is fully valid - the
    // iteration just does not continue with the remaining root moves. Without
    // this cut, an iteration started 1 ms before the soft limit would run all
    // the way to the hard limit (4x soft), overspending on nearly every move
    // and flagging in long games.
    private bool _softStopped;

    // Standalone search: owns a fresh 64 MB transposition table (the historical
    // behaviour - one search, one table).
    public AlphaBetaSearch(IPositionEvaluator evaluator)
        : this(evaluator, new TranspositionTable(sizeMb: 64)) { }

    // Lazy SMP helper worker: shares the main worker's transposition table.
    // Everything else (history, killers, search stack, evaluator) stays
    // per-instance so the threads never write the same memory except the TT.
    public AlphaBetaSearch(IPositionEvaluator evaluator, TranspositionTable sharedTt)
    {
        _evaluator = evaluator;
        _incremental = evaluator as IIncrementalEvaluator;
        _tt = sharedTt;
    }

    // The transposition table, so a Lazy SMP coordinator can share this (main)
    // worker's table with the helper workers and age it exactly once per search.
    internal TranspositionTable Tt => _tt;

    // The active evaluator, so the coordinator can Clone() it for each helper.
    internal IPositionEvaluator Evaluator => _evaluator;

    // Ages the shared table one generation. Called ONCE by the coordinator
    // before launching the pool, so helpers run with newSearch:false and do not
    // each re-age the same shared table.
    internal void NewSearchTt() => _tt.NewSearch();

    // Reallocates the transposition table ("setoption name Hash value N").
    public void ResizeTT(int sizeMb) => _tt.Resize(sizeMb);

    // Swaps the evaluator (Classical <-> NNUE). Never call during a search.
    public void SetEvaluator(IPositionEvaluator evaluator)
    {
        _evaluator = evaluator;
        _incremental = evaluator as IIncrementalEvaluator;
        _tt.Clear(); // Cached scores from another evaluator are poison.
    }

    // Clears all inter-search state (TT, killers, history). Called on
    // "ucinewgame" / GUI new game.
    public void Reset()
    {
        _tt.Clear();
        _killers.Clear();
        _history.Clear();
        foreach (ContinuationHistory level in _contHist)
            level.Clear();
        _captureHistory.Clear();
        _corrections.Clear();
        Array.Clear(_counterMoves);
        _bestPreviousScore = ScoreNone;
        _bestPreviousAverageScore = ScoreNone;
        _previousTimeReduction = 1.0;
    }

    // newSearch: age the shared TT one generation at the start (true for a
    // standalone/main search). A Lazy SMP coordinator ages the shared table
    // once itself and passes false to every worker so it is not aged N times.
    public SearchResult FindBestMove(Board board, SearchLimits limits,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null,
                                     bool newSearch = true)
    {
        if (limits.MaxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(limits), "Minimum depth is 1.");

        _nodes = 0;
        TbHits = 0;
        _rootInTb = false;
        _rootTbResolved = false;
        _stopped = false;
        _softStopped = false;
        _cancellation = cancellation;
        _hardTimeMs = limits.HardTimeMs;
        _maxTimeMs = limits.HardTimeMs; // tightened per-iteration under SMP (see below)
        _softTimeMs = limits.SoftTimeMs;
        _softDeadlineMs = limits.SoftTimeMs;
        _maxNodes = limits.MaxNodes;
        _elapsedOffsetMs = limits.ElapsedOffsetMs;
        _timer.Restart();

        // Clock mode is recognizable by soft < hard; "movetime" sets them
        // equal and the budget must then be used in full, not predictively.
        bool clockMode = limits.SoftTimeMs < limits.HardTimeMs;

        // Terminal root: checkmate or stalemate on the board. There is nothing
        // to search and, crucially, nothing to return - the iterative-deepening
        // loop below would spin through every depth without ever producing a
        // best move, and the caller would wait forever for a "bestmove" that
        // never comes. Answer at once with the game-theoretic score and a null
        // move; the UCI layer turns that into "bestmove 0000".
        // (Measured 2026-07-19: v2.7.2 and every earlier release hang outright
        // on a stalemated position - a GUI sending one froze the engine.)
        MoveGenerator.GenerateLegalMoves(board, _rootMoves);
        if (_rootMoves.Count == 0)
            return new SearchResult(Move.None, board.IsInCheck() ? -MateScore : 0, 0);
        int legalRootMoveCount = _rootMoves.Count;

        // ---- Syzygy root filtering ----
        // Knowing the position is won is not enough to WIN it: with no distance
        // to steer by the engine shuffles and draws by the fifty-move rule. DTZ
        // supplies that gradient. The root move list is therefore restricted to
        // the tablebase-optimal moves - win > draw > loss, and among wins the
        // shortest distance to the next irreversible move.
        //
        // Deliberately a FILTER and not an early return. Returning the verdict
        // straight away would replace "mate in 3" with a plain tablebase win in
        // the UCI output, undoing the mate reporting added in v2.7.1. Filtering
        // keeps the search running - so it still finds and announces the mate -
        // while making it structurally impossible to play a move that throws
        // the win away.
        if (Tablebases.Syzygy.Available
            && board.CastlingRights == CastlingRights.None
            && System.Numerics.BitOperations.PopCount(board.AllOccupancy)
               <= Math.Min(SyzygyProbeLimit, Tablebases.Syzygy.Cardinality))
        {
            FilterRootMovesByTablebase(board);

            // A decisive tablebase root needs a fraction of the clock, not all
            // of it. Every surviving move is already game-theoretically optimal
            // (or within the slack band), so the search is only choosing among
            // moves that all win; it cannot find anything better than winning.
            // Measured 2026-08-04 at 60+1: K+Q vs K spent 3129 ms per move on a
            // position with four pieces on the board, which is most of the
            // budget for a move that was decided before the search began.
            // A warm table and four pieces reach a deep, stable answer in a
            // fraction of that.
            if (clockMode && _rootTbResolved)
            {
                _softTimeMs = Math.Min(_softTimeMs, TbResolvedBudgetMs);
                _hardTimeMs = Math.Min(_hardTimeMs, TbResolvedBudgetMs * 4);
                _maxTimeMs = _hardTimeMs;
                _softDeadlineMs = _softTimeMs;
            }
        }

        // Forced move: with a single legal reply no amount of searching can
        // change the choice - answer instantly and bank the whole budget.
        // Only under a clock (analysis/movetime callers still want the eval).
        if (clockMode && legalRootMoveCount == 1)
            return new SearchResult(_rootMoves[0], 0, 0);

        // Killers are per-search (ply meanings change); history persists
        // between searches but decays so fresh information dominates. The TT
        // ages one generation: previous-search entries yield their cluster
        // slots gracefully as this search fills the table.
        _killers.Clear();
        _history.Decay();
        if (newSearch)
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
        // What the TIME MANAGER carries to the next move, which is not always
        // what the search returns. They diverge on exactly one case (a soft
        // stop inside a fail-low) and the scheduler wants the raw number there.
        int carryScore = ScoreNone;

        // Last move actually announced to the caller through 'progress', and the
        // depth it was announced at. Only COMPLETED iterations report, but an
        // interrupted iteration may still replace 'best' (see the stop handling
        // below), so without these the caller's last info line can describe a
        // different move from the one finally played. Measured over 221 bot
        // games: 2% of all moves were played with the previous depth's PV and
        // eval still standing as the last thing reported, which is exactly the
        // signal used to audit the engine from a PGN.
        Move lastReportedMove = Move.None;
        int lastReportedDepth = 0;

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

        _bestMoveChangesTotal = 0; // monotonic; coordinator reads deltas of it

        // Cap the iteration depth at the search stack's own limit. Without it,
        // an unlimited search (ponder/infinite) in a position where every
        // iteration returns instantly from the transposition table - a
        // repetition dance with a warm TT - spins the loop through ever-higher
        // depths that can no longer search anything, burning a core for the
        // whole of the opponent's thinking time. Observed in a bot game: depths
        // 22->26 completed in 30 ms with the node count barely moving. Beyond
        // MaxPly the stack cannot go deeper anyway, so this only removes the
        // degenerate spin; no real search ever reaches it.
        int maxIterationDepth = Math.Min(limits.MaxDepth, MaxPly);
        for (int depth = 1; depth <= maxIterationDepth; depth++)
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
            // Whether the window was still failing LOW when the loop ended.
            // Recorded before alpha is widened below, which would erase it.
            bool failedLow = false;
            while (true)
            {
                score = SearchRoot(board, depth, alpha, beta, out bestMove);
                failedLow = score <= alpha;
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
                // Interrupted mid-iteration. A SOFT stop lands on a root-move
                // boundary, so every move searched so far (the previous best
                // first, thanks to TT ordering) is complete and the partial
                // result is reliable - use it. A HARD stop aborts mid-node:
                // the interrupted Negamax returns 0, so if it hit during the
                // first root move, SearchRoot reports that move with score 0.
                // Trusting that 0 silently zeroed the returned score of ~half
                // of all node-limited searches (harmless to game play, which
                // reports its score from completed-iteration progress and plays
                // the same TT-first move - but it wrecked datagen labels, which
                // take the returned score). On a hard stop keep the last
                // completed iteration's result; fall back to the partial only
                // when no iteration has finished yet.
                // depth - 1: this iteration did NOT complete, so the deepest
                // fully searched one is the previous. Claiming `depth` here
                // would let a partial result outvote a complete one.
                //
                // A soft stop that lands while the window is still FAILING LOW
                // is the one case where the partial result must be thrown away.
                // Nothing was proved there: every root move came back at or
                // below alpha, so the score is an upper bound the search never
                // resolved, typically hundreds of centipawns below the last
                // completed iteration. That number then leaves by two doors -
                // it is what "info score" prints, and it is what the Lazy SMP
                // vote weighs this worker by, since the weight is the score
                // itself. A worker stopped inside a fail-low was handing the
                // vote a figure its own next re-search would have refuted.
                // The last COMPLETED iteration is the deepest thing actually
                // proved, so that is what survives; the partial is used only
                // when no iteration has finished at all and it is all there is.
                //
                // THERE IS A THIRD DOOR, and withholding the score from it was
                // a regression. `_bestPreviousScore` seeds the next move's
                // falling-eval term, so a fail-low is also how the scheduler
                // learns the position is coming apart and buys time for it.
                // Suppressing that fed a tuned time manager an input it had
                // never been tuned against, on the moves that need the time
                // most. `carryScore` therefore keeps the old value exactly:
                // the vote and the report get the proved score, the scheduler
                // still sees the drop, and at one thread - where there is no
                // vote at all - the whole change is once again a no-op.
                if (bestMove != Move.None && (_softStopped || best.BestMove == Move.None))
                {
                    carryScore = score;
                    if (best.BestMove == Move.None || !failedLow)
                        best = new SearchResult(bestMove, score, _nodes, depth - 1);
                }
                break;
            }

            best = new SearchResult(bestMove, score, _nodes, depth);
            carryScore = score;
            previousScore = score;
            progress?.Report(new SearchProgress(depth, score, _nodes, bestMove,
                                                ExtractPv(board, bestMove, depth)));
            lastReportedMove = bestMove;
            lastReportedDepth = depth;

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
            // Sum this worker's changes plus every helper's (peer func); the
            // instability factor below divides the total by the thread count to
            // get the per-thread average, matching the reference. Single-thread:
            // PeerBestMoveChanges is null and SearchThreadCount is 1 -> unchanged.
            totBestMoveChanges += _bestMoveChanges + (PeerBestMoveChanges?.Invoke() ?? 0);
            averageScore = averageScore == ScoreNone ? score : (2 * score + averageScore) / 3;

            if (clockMode)
            {
                // Proven-short-mate stop: a mate in <= 3 for us cannot get
                // shorter and a mate-in-2 loss has no longer defense, so the
                // remaining budget buys nothing. Depth-gate-free (unlike
                // easy-move), so a mate-in-1 seen at depth 1-2 plays at once
                // instead of coasting to depth 12. Excludes TB win/loss scores
                // (those sit in a band below MateScore, well under this bound).
                if (EnableProvenMateStop
                    && (score >= MateScore - MateStopGivePlies
                        || score <= -MateScore + MateStopGetPlies))
                {
                    // This path exits before the regular per-iteration debug
                    // print below ever runs, so a mate-stopped move would
                    // otherwise vanish from the NOA_TM_DEBUG trace entirely -
                    // indistinguishable from a move that simply used almost no
                    // time for some other reason. Own line, reason=mate-stop.
                    if (TimeDebug)
                        Console.Out.WriteLine(
                            $"info string TM d={depth} score={score} reason=mate-stop"
                          + $" elapsed={ElapsedMs} softDl={_softDeadlineMs}"
                          + $" maxT={_maxTimeMs} hardT={_hardTimeMs}");
                    break;
                }

                // Falling eval: when the score is dropping against the
                // previous move's average and the recent iterations, think
                // longer (the position is deteriorating and the move matters);
                // rising scores stop sooner. Constants are the reference
                // engine's with score differences rescaled to NoaChess
                // centipawns (x2.083: its internal pawn ~ 208, so a reference
                // coefficient c applies here as c * 2.083). The offset and the
                // divisor are dimensionless and stay raw.
                //
                // The previous port carried an older revision's tune (71 /
                // 25.0 / 12.5 over 656.7, clamped to [0.5, 1.5]). That clamp
                // was where the factor actually lived rather than a safety
                // rail: measured at 3+0 AND again at 180+2 in the bot's own
                // configuration, 62% of moves sat pinned at the 0.5 floor and
                // 34% at the 1.5 cap - 96% of the time this picked one of two
                // constants instead of modulating, and both sat below the
                // reference's own bounds. Combined with the reduction factor
                // it is what turns a 4052 ms budget into a 3015 ms spend.
                //
                // First move of the game: no cross-move history to compare
                // against, so use the neutral factor. (The reference maxes it
                // at 1.728 here; combined with the early-depth reduction factor
                // ~1.7 that tripled the first-move budget - visible clock
                // burn at short TC with no upside on an empty TT.)
                double fallingEval = _bestPreviousAverageScore == ScoreNone
                    ? 1.0
                    : Math.Clamp((11.48 + 4.79 * (_bestPreviousAverageScore - score)
                                        + 2.29 * (_iterValue[iterIdx] - score)) / 100.0,
                                 0.576, 1.728);

                // Stability: the longer the best move has held, the less time
                // the position needs; the factor also carries over to the next
                // move via previousTimeReduction.
                //
                // This used to be a cliff at exactly 10 iterations (1.37 above,
                // 0.65 below), which made a move stable for 9 iterations
                // indistinguishable from one that had just changed, and one
                // stable for 10 indistinguishable from one stable for 30. In
                // the same measurements the resulting 'reduction' collapsed
                // onto a single value on 85-90% of moves. The reference ramps
                // it linearly over the stability age instead, which is what the
                // two endpoints below encode; the clamp flattens it outside
                // roughly [5, 17] iterations of age.
                double stableAge = depth - lastBestMoveDepth;
                timeReduction = Math.Clamp(
                    0.639 + (1.712 - 0.639) * (stableAge - 4.96) / (18.79 - 4.96),
                    0.629, 1.544);
                double reduction = (1.468 + _previousTimeReduction) / (2.284 * timeReduction);

                // Instability: each root best-move change (decayed per
                // iteration) extends the budget. Neutral on the first move of
                // the game: on an empty TT the root flaps between near-equal
                // openings, and that flapping carries no urgency signal -
                // extending for it just burns the clock before the game starts.
                //
                // The base is 1.077, not 1.0: the reference spends slightly
                // over the nominal budget even on a perfectly stable root, and
                // 93% of measured moves saw no root change at all, so that
                // 7.7% applies to nearly every move of a game.
                double bestMoveInstability = _bestPreviousAverageScore == ScoreNone
                    ? 1.0
                    : 1.077 + 2.229 * totBestMoveChanges / SearchThreadCount;

                double totalTime = _softTimeMs * fallingEval * reduction * bestMoveInstability;

                // Multi-thread safety cap on the soft deadline. Under the
                // shared-TT races the dynamic factors (falling-eval, reduction,
                // instability) can inflate at once and spike the budget toward
                // the hard maximum on a trivial move.
                //
                // This used to clamp at the optimum itself, which made the whole
                // scheduler one-sided: every reduction applied in full while
                // every extension was thrown away, because the bot always runs
                // with more than one thread. The engine could only ever think
                // LESS than the target, never more, so a falling eval or a
                // flapping best move - the two signals that say "this position
                // is dangerous, look harder" - did nothing at all. Measured
                // 2026-08-02 over 49 games on the Mac: it finished with 73% to
                // 98% of the clock unused depending on the time control, and
                // zero losses on time.
                //
                // Bound the extension at twice the optimum instead. The spike is
                // still contained (the hard maximum sits at 4x to 7x, and the
                // mid-iteration _maxTimeMs guard below is the real brake), but
                // the position-danger signals can once again buy thinking time.
                if (SearchThreadCount > 1 && totalTime > _softTimeMs * SmpExtensionCap)
                    totalTime = _softTimeMs * SmpExtensionCap;

                // Easy move: a decisively winning/losing score (including a
                // found mate/tablebase result, whose magnitude is far above the
                // margin) that has not changed the best move for several
                // iterations will not change. Play it on a fraction of the
                // budget and bank the clock rather than spend the full optimum
                // on an obvious move. Only after a trustworthy depth.
                bool easyMoveEligible = depth >= EasyMoveMinDepth
                    && Math.Abs(score) >= EasyMoveMargin
                    && lastBestMoveDepth + EasyMoveStableDepth <= depth;
                if (easyMoveEligible)
                    totalTime = Math.Min(totalTime, _softTimeMs * EasyMoveFraction);

                // Diagnostic for the time manager, off unless NOA_TM_DEBUG=1.
                // Prints the target, every factor applied to it, the score and
                // what has actually been spent, so a disagreement between the
                // arithmetic and the behaviour is visible instead of guessed
                // at. 'reason' distinguishes the two named early-exit paths
                // (mate-stop has its own line above, since it breaks before
                // reaching here) from a move that just naturally settled under
                // budget for some OTHER reason - a TT-saturated, heavily
                // repeated self-play position resolving in a couple of cheap
                // iterations, for instance - which this alone cannot name, but
                // ruling out the two known causes narrows it down by elimination.
                string reason = easyMoveEligible ? "easy-move" : "budget";
                if (TimeDebug)
                    Console.Out.WriteLine(
                        $"info string TM d={depth} score={score} reason={reason}"
                      + $" soft={_softTimeMs} fe={fallingEval:F3}"
                      + $" red={reduction:F3} inst={bestMoveInstability:F3}"
                      + $" total={totalTime:F0} elapsed={ElapsedMs}"
                      + $" softDl={_softDeadlineMs} maxT={_maxTimeMs} hardT={_hardTimeMs}");

                // Stop if past the modulated budget; otherwise it becomes the
                // deadline the next iteration's root-boundary checks use.
                if (ElapsedMs > totalTime)
                    break;
                _softDeadlineMs = (long)totalTime;

                // Mid-iteration overshoot guard. The root-boundary soft-stop
                // (SearchRoot) only fires BETWEEN root moves, so a single deep
                // root move begun near the budget edge can run for many seconds
                // to the loose hard maximum before the next check - a won
                // position reaches high depth almost instantly, so this burned
                // 8.5 s at 5+5 on an obvious recapture whose mate was already
                // seen (and 22-37 s on a ponderhit at 30 threads). Tighten the
                // NODE-level deadline to a small multiple of the (dynamic) soft
                // budget: CheckStop then aborts the runaway move mid-iteration
                // and the hard-stop keeps the last completed iteration's move.
                // Never above the hard maximum; applies to single-thread and SMP.
                // Fixed-depth/analysis is unaffected (this whole block is clock
                // mode only), so those node counts stay byte-identical.
                _maxTimeMs = Math.Min(_hardTimeMs, (long)(totalTime * OvershootFactor));
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
                // carryScore, not best.Score: see the fail-low note in the
                // iteration loop. These two feed the falling-eval term and must
                // keep seeing the drop the returned result no longer carries.
                int carried = carryScore == ScoreNone ? best.Score : carryScore;
                _bestPreviousScore = carried;
                _bestPreviousAverageScore = averageScore == ScoreNone ? carried : averageScore;
            }
        }

        // Extreme fallback (e.g. cancelled before depth 1 finished - a cold
        // process under a tiny first-move budget): never return "no move" while
        // legal moves exist. Instead of the FIRST generated move (move ordering
        // makes that a rook-pawn push, which looks absurd), pick the move with
        // the best static eval - a one-ply search that costs one eval per legal
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

        // Announce the move actually being returned whenever it is not the one
        // the last completed iteration reported. An interrupted iteration and
        // the static fallback above both replace 'best' without going through
        // the per-iteration report, which left the caller holding a PV and an
        // evaluation belonging to a DIFFERENT move - the annotated PGN then
        // shows a variation that does not start with the move played, and the
        // recorded eval belongs to the discarded line. Purely a reporting fix:
        // the search itself is untouched, and callers that pass no progress
        // sink (datagen, tests, fixed-depth) are byte-identical.
        if (progress is not null && best.BestMove != Move.None
            && best.BestMove != lastReportedMove)
        {
            int reportDepth = Math.Max(1, lastReportedDepth);
            progress.Report(new SearchProgress(reportDepth, best.Score, _nodes, best.BestMove,
                                               ExtractPv(board, best.BestMove, reportDepth)));
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
            contHist: default, counterMove: Move.None,
            captureHistory: _captureHistory);

        int bestScore = -Infinity;
        int searched = 0;
        // alpha is raised in the loop below, so the bound test at the store
        // needs the window this node actually started with.
        int originalAlpha = alpha;

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
                // The root is a PV node; its first child stays on the PV.
                score = -Negamax(board, depth - 1, -beta, -alpha, ply: 1, allowNull: true,
                                 cutNode: false);
            }
            else
            {
                // Scout children of the PV root are expected cut nodes.
                score = -Negamax(board, depth - 1, -alpha - 1, -alpha, ply: 1, allowNull: true,
                                 cutNode: true);
                if (score > alpha && !_stopped)
                    score = -Negamax(board, depth - 1, -beta, -alpha, ply: 1, allowNull: true,
                                     cutNode: false);
            }

            board.UnmakeMove();
            _incremental?.Pop();
            searched++;

            // A score computed after the stop signal is garbage; only use it
            // if we have nothing at all yet.
            if (_stopped && bestMove != Move.None)
                break;

            // A scout score is only a bound. A root move that failed low was
            // never re-searched with the full window, so its value is not
            // comparable with the first move's exact score and must not be
            // allowed to take the lead - otherwise a fail-low iteration can
            // hand back a move the search never actually endorsed, while the
            // reported PV still shows the previous (real) best.
            if (score > bestScore && (searched == 1 || score > alpha))
            {
                bestScore = score;
                bestMove = move;

                // Best-move change bookkeeping for the time manager: a root
                // move other than the first one taking over the lead signals
                // an unstable position that deserves more time. The monotonic
                // counter feeds the cross-thread peer delta (Lazy SMP).
                if (searched > 1)
                {
                    _bestMoveChanges++;
                    _bestMoveChangesTotal++;
                }

                if (score > alpha)
                    alpha = score;
            }

            if (alpha >= beta)
                break;

            // Soft time boundary: root moves are the only place where the
            // search can stop "gracefully" - everything searched so far is
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
        // Three-way bound, as the inner nodes already do. The old two-way test
        // filed a fail-low root (every move at or below the aspiration window)
        // as Exact, which is a score the search never proved: an aspiration
        // fail-low is an UPPER bound and nothing more.
        if (!_stopped && !_softStopped)
        {
            BoundType bound = bestScore <= originalAlpha ? BoundType.UpperBound
                            : bestScore >= beta ? BoundType.LowerBound
                            : BoundType.Exact;
            _tt.Store(board.ZobristKey, depth, ToTT(bestScore, 0), TTEntry.NoStaticEval,
                      bound, bestMove, isPv: true);
        }

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

        // WDL by default, DTZ only when the fifty-move rule is close enough to
        // matter.
        //
        // DTZ is the distance to the next IRREVERSIBLE move, not to mate. On a
        // board with no pawns and nothing of the opponent's to capture, the
        // only move that can shorten it is letting the opponent capture one of
        // OUR pieces. In K+Q+Q vs K that makes hanging a queen the
        // "DTZ-optimal" move, and restricting the root to DTZ-optimal moves
        // forced the engine to play it. Measured 2026-08-02 on
        // 8/8/8/5K2/8/2k5/8/4q1q1 b: mate in 3 with the tables switched off, a
        // queen sacrifice with them on. Same binary, same position.
        //
        // WDL ranking keeps every move that preserves the game-theoretic
        // result, so the win still cannot be thrown away, and the search picks
        // the fastest mate among them (it can, now that _rootInTb stops the
        // flat in-search tablebase scores). DTZ only takes over once the
        // halfmove clock is high enough that making progress towards a zeroing
        // move is what actually saves the win.
        // Refined 2026-08-03 after the first version caused the opposite bug.
        // DTZ steers towards the next irreversible move, and that is exactly
        // what a won endgame wants WHEN there is a pawn to push: the push zeroes
        // the counter and is genuine progress towards promotion. DTZ is only
        // harmful with NO pawns of ours and nothing of theirs to capture,
        // because then the sole way to shorten it is to let them take one of
        // ours - the K+Q+Q vs K queen sacrifice.
        //
        // Ranking by WDL in every position below the urgency clock produced a
        // real game against zipfile_chess-bot (lichess.org/HUAC6sVf): K+N+3P vs
        // K, twenty moves of knight and king shuffling from move 123, then the
        // halfmove counter hit the threshold, DTZ engaged, f6-f7-f8=Q and mate
        // in five. Everything needed to win was already there; only the reason
        // to make progress was missing.
        // DTZ first, always, with WDL only as the fallback when the .rtbz files
        // cannot answer. See the slack band below for how the queen sacrifice
        // is avoided without giving up the progress DTZ provides.
        int bestRank;
        if (!TryRankRootMovesByDtz(board, ranks, out bestRank)
            && !TryRankRootMovesByWdl(board, ranks, out bestRank))
            return;

        TbHits++;
        _rootTbResolved = true;

        // The ranking covered every root move, so whatever survives below is
        // game-theoretically optimal. From here the search runs WITHOUT
        // tablebase scores (see the probe guard in Negamax): it is the only
        // thing left that can tell two equally-winning moves apart.
        //
        // Only for a DECISIVE root, though. A flat score is a problem when it
        // hides which win is fastest; on a drawn position zero is simply the
        // truth, and switching the probe off would make the search report a
        // fantasy evaluation for a position the tables call dead. Measured on
        // 8/8/8/4k3/8/8/4KNN1/8 w (K+N+N vs K, which cannot be forced): +531
        // instead of 0. The move stayed safe because the filter still ran, but
        // the score feeds the UCI output and anything reading it, including the
        // draw-offer rule in the bot.
        _rootInTb = Tablebases.Syzygy.ProbeWdl(board, out var rootWdl)
                 && rootWdl is Tablebases.WdlScore.Win or Tablebases.WdlScore.Loss;

        // Keep every move with the best TB rank, so the search still gets a
        // choice among equally optimal continuations.
        // Keep a BAND of ranks, not only the exact best.
        //
        // For a certain win the rank is MaxDtz - dtz, so a slack in rank units
        // is a slack in plies-to-the-next-irreversible-move. Insisting on the
        // single best DTZ is what made K+Q+Q vs K hang a queen: letting the
        // opponent capture one zeroes the counter in 1 ply, which beats mating
        // in 3, so the "optimal" move was the only one kept and the search had
        // no say. Widening the band keeps the mating moves as well, and since
        // _rootInTb switches off the flat in-search tablebase scores, the search
        // sees the real mate distance and picks it.
        //
        // The slack shrinks as the fifty-move counter climbs: freedom while
        // there is room, strict obedience to DTZ when the draw is close. That
        // is the guarantee that matters, and it is kept exactly where it counts.
        int slack = bestRank > MaxDtz / 2
            ? Math.Max(0, (90 - board.HalfmoveClock) / 8)
            : 0;

        var keep = new MoveList();
        for (int i = 0; i < n; i++)
            if (ranks[i] >= bestRank - slack)
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
                        bool cutNode, Move excluded = default)
    {
        if ((++_nodes & (StopCheckInterval - 1)) == 0)
            CheckStop();
        if (_stopped)
            return 0;

        // Ply overflow guard for recursive and singular-extension searches.
        if (ply >= MaxPly)
            return _evaluator.Evaluate(board);

        // NOTE: the check test is NOT computed here. It is two magic-bitboard
        // lookups into multi-megabyte tables, and everything between this point
        // and the static evaluation below can return first: the transposition
        // cutoff, the tablebase probe, and above all the depth<=0 delegation to
        // quiescence - which computes the same thing again on the same board.
        // The two rare paths that need it before then ask for it themselves.

        // ---- Draws by rule. Checked before the TT: a cached score cannot
        // know the path's repetition count or fifty-move clock. Checkmate has
        // precedence at clock 100, so an in-check node first proves that an
        // escape exists; this path is exceptionally rare and can afford the
        // allocation-free legal-move probe.
        if (board.HalfmoveClock >= 100)
        {
            if (!board.IsInCheck() || MoveGenerator.HasLegalMove(board, _moveLists[ply]))
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
        //
        // Skipped entirely when the ROOT was already resolved by the tables
        // (_rootInTb). A tablebase score is flat: every winning continuation
        // returns exactly TbWin - ply, so the search cannot rank them and keeps
        // whichever it happened to try first. In K+P vs K that made promoting to
        // a rook score identical to promoting to a queen - both simply "win" -
        // and the engine underpromoted. The root move list is already restricted
        // to DTZ-optimal moves at that point, so the win cannot be thrown away;
        // turning the probe off lets normal evaluation and mate distance pick
        // the fastest win among the moves the tables call equal.
        int pieceCount = System.Numerics.BitOperations.PopCount(board.AllOccupancy);
        if (!_rootInTb
            && pieceCount <= _tbMaxMen
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
        // position was never searched or its entry was overwritten - move
        // ordering will be poor and the full depth is not worth its cost.
        // Search one ply shallower; if the node matters, a later (deeper)
        // visit will find a TT move waiting and search it properly.
        if (depth >= 4 && ttMove == Move.None && excluded == Move.None)
            depth--;

        // ---- Horizon: switch to quiescence instead of a raw evaluation ----
        if (depth <= 0)
            return Quiescence(board, alpha, beta, ply);

        // Only now, past every early return above. Same board, so the same
        // answer as computing it at the top - just not paid by the nodes that
        // never reach here.
        bool inCheck = board.IsInCheck();

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
        // the NEXT visit - often via IIR or a re-search - skips it too.
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

            staticEval = _corrections.Correct(board, rawStaticEval, ContinuationCorrectionKey(ply));
        }

        // ---- Improvement / improving ----
        // How much our static eval gained over our previous position - two
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
        // this line - return without searching. Only at shallow depth and away
        // from mate scores, where the static eval is a trustworthy proxy.
        // An improving eval is trending up and can be trusted one depth-step
        // sooner (reference: margin × (depth - improving)); the parent move's
        // statScore leans on the margin - after a well-reputed parent move the
        // cut comes easier, after a maligned one it needs more headroom.
        if (!inCheck && nonPv && depth <= 6 && Math.Abs(beta) < MateBound
            && excluded == Move.None
            && staticEval >= beta
            && staticEval - 85 * (depth - (improving ? 1 : 0))
               - (ply > 0 ? _stackStatScore[ply - 1] : 0) / StatScoreRfpDiv >= beta)
            return staticEval;

        // ---- Null Move Pruning (with verification search) ----
        // "Pass" the turn: if the opponent moving twice in a row still cannot
        // bring us below beta, no real move will either - prune the branch.
        // Reference entry condition: non-PV only, never in check or right
        // after another null (the position must re-anchor between passes),
        // never without non-pawn material (zugzwang), only when the static
        // eval clears beta by a margin that shrinks with depth and improvement
        // and grows with complexity, and not while the parent move's statScore
        // says the parent is already refuting everything cheaply. During a
        // verification search the verifying side cannot null again below
        // nmpMinPly (a false null cutoff must not verify itself).
        // Entry: the previously validated shape (any node at depth >= 3, no
        // eval precondition - a cheap probe everywhere). The reference gates
        // entry on staticEval >= beta plus a depth/improvement/complexity
        // margin and a statScore filter; measured here, that gating grows the
        // tree ~30% at equal tactics because our classical eval is noisy
        // relative to the search - probes at eval-below-beta nodes keep
        // finding real cutoffs the gate would forbid. Revisit with NNUE.
        if (allowNull && !inCheck && depth >= 3 && ply > 0 && excluded == Move.None
            && board.HasNonPawnMaterial(board.SideToMove)
            && (ply >= _nmpMinPly || board.SideToMove != _nmpColor))
        {
            // Reduction: the previously validated shape (child depth
            // depth - 3 - depth/4). The reference's deeper dynamic R
            // (min((eval-beta)/168, 7) + depth/3 + 4 - (complexity > 861))
            // is DEFERRED to 5C+: its null probes bottom out in quiescence
            // across depths 3-7, and OUR quiescence is captures-only - the
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
                                     ply + 1, allowNull: false, cutNode: false);
            board.UnmakeNullMove();
            _incremental?.Pop();

            if (_stopped)
                return 0;

            if (nullScore >= beta && nullScore < MateBound)
            {
                // Mate-range null scores never cut (the guard above): a mate
                // "found" after passing a move is exactly the unproven kind -
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
                int v = Negamax(board, depth - r, beta - 1, beta, ply, allowNull: false,
                                cutNode: cutNode);
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

                // Read the MOVING piece before the move is made, exactly as the
                // root and the main move loop do. Reading it off the
                // destination afterwards returns the PROMOTED piece for a
                // promotion, so a queen promotion filed its continuation
                // history under Queen while every other path files the same
                // move under Pawn - the split key this file warns about at
                // ContinuationCorrectionKey, and it is silent when it happens.
                int movedPiece = ContinuationHistory.PieceIndex(mover, board.PieceTypeAt(move.From));
                _incremental?.PushMove(board, move);
                board.MakeMove(move);
                if (board.IsSquareAttacked(board.KingSquare(mover), board.SideToMove))
                {
                    board.UnmakeMove();
                    _incremental?.Pop();
                    continue;
                }
                _stackPiece[ply] = movedPiece;
                _stackTo[ply] = move.To;
                _stackStatScore[ply] = 0;

                int score = -Quiescence(board, -probBeta, -probBeta + 1, ply + 1);
                if (score >= probBeta)
                    score = -Negamax(board, probCutDepth, -probBeta, -probBeta + 1,
                                     ply + 1, allowNull: false, cutNode: !cutNode);

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
        // Depth >= 1 is not redundant. Quiescence now writes its own results at
        // depth 0 with a real bound, and at depth 4 the "depth - 4" test alone
        // would let a quiescence score - a truncated, captures-only search -
        // stand in for a ProbCut verification. Only entries from a real search
        // qualify here.
        if (!inCheck && excluded == Move.None && ttHit
            && entry.Bound == BoundType.LowerBound
            && entry.Depth >= 1 && entry.Depth >= depth - 4
            && Math.Abs(beta) < MateBound)
        {
            int ttScore = FromTT(entry.Score, ply);
            if (ttScore >= smallProbBeta && Math.Abs(ttScore) < MateBound)
                return smallProbBeta;
        }
        // ---- Singular extension detection ----
        // A TT move whose stored score is trustworthy gets a verification
        // search: all OTHER moves are searched shallower against a lowered
        // window. If none comes close, the TT move is "singular" - the only
        // move holding the position - and deserves an extra ply, because
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
                                    ply, allowNull: false, cutNode: cutNode, excluded: ttMove);
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
        //   stage 0: the TT move alone, vetted by IsPseudoLegal - no generation.
        //   stage 1: captures/promotions, sorted; served while SEE-good.
        //   stage 2: quiet moves (sorted with any unserved losing captures,
        //            which sink to the very end by score band).
        // The order served is identical to the old full-sort ordering.
        MoveList moves = _moveLists[ply];
        moves.Clear();
        bool ttServed = ttMove != Move.None && MoveGenerator.IsPseudoLegal(board, ttMove);
        if (ttServed)
            moves.Add(ttMove);

        // If the TT's best move is itself a capture, quiet alternatives are less
        // likely to be the refutation, so late quiets are reduced one extra ply
        // in the LMR block below. Gated on ttServed so the flag is known coherent
        // with this position (pseudo-legality already validated), matching the
        // reference's capture_stage(ttMove) rather than a stale stored flag.
        bool ttCapture = ttServed && (ttMove.IsCapture || ttMove.IsPromotion);

        // Previous-move context for counter moves and continuation history
        // (absent at the root or right after a null move).
        int prevPiece = ply > 0 ? _stackPiece[ply - 1] : -1;
        int prevTo = prevPiece >= 0 ? _stackTo[ply - 1] : 0;
        Move counterMove = prevPiece >= 0 ? _counterMoves[(prevPiece * 64) + prevTo] : Move.None;

        var contHist = new ContinuationContext(
            prevPiece >= 0 ? _contHist[0] : null, prevPiece, prevTo);

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
        // Per-ply slices of a preallocated buffer, not stackalloc. A stackalloc
        // is ZERO-INITIALISED on every call - 224 bytes here, at every node, for
        // data that is always written before it is read. The per-ply aliasing is
        // the same one _moveLists already relies on and is safe for the same
        // reason: the singular verification search runs at this same ply but
        // returns before the move loop below writes anything, and every read is
        // bounded by the local count.
        Span<Move> triedCaptures = _triedCaptures.AsSpan(ply * MaxTriedCaptures, MaxTriedCaptures);
        int triedCaptureCount = 0;
        Span<Move> triedQuiets = _triedQuiets.AsSpan(ply * MaxTriedQuiets, MaxTriedQuiets);
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
                        _killers, _history, ply, contHist, counterMove,
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

            // NOTE for whoever ports the reference's lmrDepth-scaled pruning
            // margins: an `lmrDepth` used to be estimated here - a full
            // LmrReductions lookup plus two conditionals and a division, on
            // every move - and NOTHING read it. It existed for the reshape the
            // futility block below documents as deferred, and was left behind
            // when that was cut. Recompute it here if the reshape is ever
            // attempted; until then it is pure cost.

            // ---- Forward pruning of quiet moves (shallow, non-PV, not in
            //      check, at least one move already searched so a best move is
            //      guaranteed) ----
            if (isQuiet && searched > 0 && nonPv && !inCheck && Math.Abs(alpha) < MateBound)
            {
                // Late move pruning: once enough quiet moves have been tried at
                // low depth, the remaining ones are very unlikely to be best.
                // In a worsening position quiet moves rarely save the node -
                // halve the count before the cut (reference LMP shape).
                int lmpThreshold = 3 + depth * depth;
                if (!improving) lmpThreshold /= 2;
                if (depth <= 3 && quietsSearched >= lmpThreshold)
                    continue;

                // Futility pruning (reference parent-node shape): if the static
                // eval plus a margin that grows with the LMR-reduced depth
                // cannot reach alpha, the quiet move will not rescue the node.
                // The move's own history buys a reprieve - a move the tables
                // like must not be pruned on eval alone (values 106/145 at
                // their x0.48 equivalents, history divisor unit-rescaled).
                // Futility pruning: if even a generous per-ply margin over the
                // static eval cannot lift it to alpha, a quiet move will not
                // rescue the node - skip it. The reference's lmrDepth-scaled
                // reshape (106 + 145*lmrDepth up to lmrDepth 13) is DEFERRED
                // to 5C: it presupposes the reference's larger LMR reductions
                // (which keep its lmrDepth systematically lower) - measured
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

            // The move's combined history signal (2x butterfly + continuation
            // history). Computed HERE, past every pruning test above, because
            // it is only ever read by the stack write below: a pruned or
            // illegal move used to pay both table reads for nothing, and the
            // continuation-history table is 2.3 MB, so that lookup is a likely
            // cache miss bought for a move that is never made.
            int movePieceIdx = ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(move.From));
            int moveHistory = 2 * _history.Get(stm, move)
                + (prevPiece >= 0 ? _contHist[0].Get(prevPiece, prevTo, movePieceIdx, move.To) : 0);

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
                // PVS: the first (best-ordered) move gets the full window and,
                // as a PV child, is never a cut node.
                score = -Negamax(board, newDepth, -beta, -alpha, ply + 1, allowNull: true,
                                 cutNode: false);
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
                    int r = LmrReductions[(Math.Min(depth, 63) * 64) + Math.Min(searched, 63)];
                    if (nonPv) r += LmrScale;            // Reduce harder off the PV.

                    // No butterfly-history term here - three variants were tested
                    // and rejected; see the LmrReductions block above. "This move
                    // is good" is expressed only through the killer/counter
                    // shallowing below, which is measured net positive.
                    if (move == counterMove || _killers.Rank(ply, move) > 0)
                        r -= LmrScale;

                    // 5C adjuster (shipped, +7.1 Elo): reduce quiet moves one
                    // extra ply when the TT best move is a capture. Reference
                    // value 1079 in 1024ths (~1.05 plies); a reduction is measured
                    // in plies so neither the value-unit nor history-unit scaling
                    // applies.
                    if (ttCapture) r += 1079;

                    // NO cutNode reduction term. The reference's largest LMR
                    // adjuster (r += 4026 at cut nodes) was measured at two
                    // magnitudes and rejected both times: 4026 (~3.9 plies) at
                    // −4.0 ±10.8 H0, 1536 (~1.5 plies) at −7.1 ±12.5 H0 - losing
                    // ~5 Elo regardless of strength, the two intervals overlapping.
                    // Most likely our cut-node classification is noisier than the
                    // reference's (no IIR, thinner node-type discipline), so the
                    // adjuster reduces the wrong nodes at any magnitude. cutNode
                    // stays THREADED (behaviour-neutral) because allNode/cutoffCnt
                    // adjusters need it, but it drives no reduction directly.

                    // 5C adjuster under test: reduce LESS at ttPv nodes (a node
                    // that was on a previous search's principal variation is worth
                    // searching more carefully). Reference removes 3023 + 1004*PV
                    // + 885*(ttValue>alpha) + 816*(ttDepth>=depth) [+940*cutNode].
                    // Scaled ×0.34 so the base is ~1 ply instead of ~3: at 3 plies
                    // it would floor our milder reductions to zero at every ttPv
                    // node and the conditionals would stop modulating. The cutNode
                    // sub-term is dropped - our cut-node signal is too noisy to
                    // trust (see above). Conditionals gated on ttHit so ttValue and
                    // ttDepth are real.
                    if (ttPv)
                    {
                        r -= 1024 + (nonPv ? 0 : 340);
                        if (ttHit)
                        {
                            if (entry.Bound != BoundType.None && FromTT(entry.Score, ply) > alpha)
                                r -= 300;
                            if (entry.Depth >= depth) r -= 277;
                        }
                    }

                    // Position is worsening: the remaining moves are even less
                    // likely to be good - reduce them one extra ply.
                    if (!improving) r += LmrScale;

                    reduction = r / LmrScale;
                    if (reduction < 0) reduction = 0;
                    if (reduction > newDepth - 1) reduction = newDepth - 1;
                }

                // PVS null window (cheap refutation attempt), possibly reduced.
                // A reduced LMR probe is searched as an expected cut node; an
                // unreduced scout mirrors the reference's non-LMR path (!cutNode).
                score = -Negamax(board, newDepth - reduction, -alpha - 1, -alpha,
                                 ply + 1, allowNull: true,
                                 cutNode: reduction > 0 ? true : !cutNode);

                // The reduced probe beat alpha: verify at full depth first
                // (reference re-search flips the parent's node type).
                if (score > alpha && reduction > 0 && !_stopped)
                    score = -Negamax(board, newDepth, -alpha - 1, -alpha,
                                     ply + 1, allowNull: true, cutNode: !cutNode);

                // Still inside the window: it is a genuine PV candidate,
                // re-search with the real window as a PV (non-cut) child.
                if (score > alpha && score < beta && !_stopped)
                    score = -Negamax(board, newDepth, -beta, -alpha,
                                     ply + 1, allowNull: true, cutNode: false);
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
                        // it get a malus - they had their chance and failed.
                        if (isQuiet)
                        {
                            _killers.Store(ply, move);
                            _history.AddBonus(stm, move, depth);

                            // movePieceIdx already holds exactly this: it was
                            // read from the same square of the same position,
                            // and the board has been unmade above.
                            if (prevPiece >= 0)
                            {
                                _counterMoves[(prevPiece * 64) + prevTo] = move;
                                _contHist[0].AddBonus(prevPiece, prevTo, movePieceIdx, move.To, depth);
                            }

                            for (int q = 0; q < triedQuietCount; q++)
                            {
                                Move tried = triedQuiets[q];
                                if (tried == move)
                                    continue;
                                _history.AddMalus(stm, tried, depth);
                                int triedPiece =
                                    ContinuationHistory.PieceIndex(stm, board.PieceTypeAt(tried.From));
                                if (prevPiece >= 0)
                                    _contHist[0].AddMalus(prevPiece, prevTo, triedPiece, tried.To, depth);
                            }
                        }
                        else if (move.IsCapture)
                        {
                            // A capture produced the cutoff: it earns capture
                            // history, which is what the quiescence capture
                            // ordering reads. The board is restored here, so
                            // the victim is back on its square.
                            _captureHistory.AddBonus(
                                movePieceIdx,
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
        // excluded TT move is the only legal move - report a fail-low so the
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
                _corrections.Update(board, bestScore - staticEval, depth,
                                    ContinuationCorrectionKey(ply));
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
    // gives check lands the opponent in exactly this node - so the hole sat on
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
    // - stand-pat beta softening (441/583 and 462/562 in 1024ths), futilityBase
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

        // Same reasoning as the main search: the check test is two magic-bitboard
        // lookups and the transposition cutoff below can finish the node without
        // it, which v4.4.0 made the common case by giving quiescence a TT probe
        // at all. The two rare paths that need it first ask for it themselves.

        // Ply ceiling, checked before the per-ply move list is indexed.
        if (ply >= MaxPly)
            return board.IsInCheck() ? 0 : _evaluator.Evaluate(board);

        // Quiet check evasions can reach clock 100 or complete a repetition,
        // so every qsearch node must enforce the rules, not only checked ones.
        // As in the main search, a mate on the 100th halfmove wins before the
        // draw claim and is verified by the rare legal-move probe.
        if (board.HalfmoveClock >= 100)
        {
            if (!board.IsInCheck() || MoveGenerator.HasLegalMove(board, _moveLists[ply]))
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

        // Quiescence used to ignore the transposition table completely, so
        // every node here paid a full NNUE evaluation for its stand-pat even
        // when the position had already been evaluated elsewhere in the tree.
        // Profiling (2026-08-07) put 28.7% of engine time inside
        // NnueInference.EvaluateInt16 and 31.6% inside quiescence; this is
        // where the two overlap. The main search has cached its static eval in
        // the TT since 5F - "revisits pay one cluster read, not a full
        // evaluation" - and quiescence simply never got the same treatment.
        //
        // Static eval ONLY. No TT score cutoff and no TT move for ordering:
        // both of those change WHICH nodes get searched, so they are a
        // separate change with its own measurement, not a speed patch.
        bool ttHit = _tt.Probe(board.ZobristKey, out TTEntry entry);
        bool nonPv = beta - alpha == 1;
        bool ttPv = !nonPv || (ttHit && entry.IsPv);

        // Reference qsearch step 3: a stored bound from ANY earlier search of
        // this position - main search or quiescence - already answers the
        // question when it covers the window, so the node is finished before a
        // single move is generated. Off the PV only, where a wrong cut cannot
        // corrupt the reported line.
        //
        // No depth test is needed. The reference has to write DEPTH_UNSEARCHED
        // (-2) on its eval-only entries and compare against DEPTH_QS (0) to
        // tell them apart; TTEntry.Depth here is a BYTE and cannot hold a
        // negative, so this engine marks eval-only entries with
        // BoundType.None instead. Excluding that bound is the same test.
        if (nonPv && ttHit && entry.Bound != BoundType.None
            && CanReuseTtScore(entry.Score, board.HalfmoveClock))
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

        int bestScore;
        int futilityBase;
        int rawEval = TTEntry.NoStaticEval;
        Move bestMove = Move.None;

        // Only now, past the transposition cutoff above.
        bool inCheck = board.IsInCheck();

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
            // beats beta, the opponent will avoid this line - cut immediately.
            if (ttHit && entry.StaticEval != TTEntry.NoStaticEval)
            {
                rawEval = entry.StaticEval;
            }
            else
            {
                rawEval = _evaluator.Evaluate(board);
                // Cache it in an eval-only entry (depth 0, no bound, no move),
                // exactly as the main search does on a miss, so the next visit
                // to this position - from either search - skips the evaluator.
                _tt.Store(board.ZobristKey, 0, 0, rawEval,
                          BoundType.None, Move.None, ttPv);
            }

            bestScore = _corrections.Correct(board, rawEval, ContinuationCorrectionKey(ply));

            // A stored SCORE beats the static evaluation as a stand-pat floor
            // when its bound points the right way: it came from a real search
            // of this position, the eval is only a guess about it. Decisive
            // scores are excluded - a mate or tablebase value is relative to
            // its own root distance and does not belong in a stand-pat.
            if (ttHit && entry.Bound != BoundType.None
                && CanReuseTtScore(entry.Score, board.HalfmoveClock))
            {
                int ttScore = FromTT(entry.Score, ply);
                bool pointsUp = entry.Bound is BoundType.LowerBound or BoundType.Exact;
                bool pointsDown = entry.Bound is BoundType.UpperBound or BoundType.Exact;
                if (Math.Abs(ttScore) < MateBound
                    && (ttScore > bestScore ? pointsUp : pointsDown))
                    bestScore = ttScore;
            }

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
                contHist: default, counterMove: Move.None,
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
            // save or a draw into a reported mate. Promotions are exempt too -
            // the piece changes mid-sequence, which the swap algorithm cannot
            // model, and an underpromotion is sometimes the only move that
            // avoids stalemate, delivers mate or dodges a fork. All four
            // promotion pieces are searched (the reference does not drop the
            // minors either); the ordering ranks the queen first, so the
            // others only cost the tail of the list.
            if (!inCheck && !move.IsPromotion)
            {
                // Futility: even winning the piece standing on the destination
                // square, plus a generous margin, cannot reach alpha - the
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

            _incremental?.PushMove(board, move);
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
                    bestMove = move;
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
            // longest defense - the same convention the main search uses.
            if (inCheck)
                return -MateScore + ply;

            // Exact stalemate. The old king-and-pawns shortcut missed legal
            // stalemates with a pinned minor. HasLegalMove generates into the
            // already-owned ply buffer and stops at the first legal quiet, so
            // correctness does not require allocating or filtering a full list.
            if (!MoveGenerator.HasLegalMove(board, moves))
                return 0;
        }

        // Save what this node learned, so the next visit can take the cutoff
        // above instead of repeating the whole capture sequence. Depth 0 with
        // a REAL bound is what distinguishes a quiescence result from the
        // eval-only entries written further up (BoundType.None).
        //
        // Never EXACT: quiescence searches a truncated move list, so its score
        // is a bound on the true value, never the value itself. The reference
        // stores LOWER or UPPER here for the same reason.
        //
        // The mate-score paths above return without storing - a mate found
        // here is relative to this ply and the store would need the root
        // distance folded in, which is what ToTT does but only for scores this
        // node actually searched for.
        _tt.Store(board.ZobristKey, 0, ToTT(bestScore, ply), rawEval,
                  bestScore >= beta ? BoundType.LowerBound : BoundType.UpperBound,
                  bestMove, ttPv);

        return bestScore;
    }

    // Reconstructs the principal variation (the expected best play for both
    // sides) by walking the transposition table: play the best move, look up
    // the resulting position's stored best move, and repeat. The PV may be
    // shorter than the search depth when TT entries were overwritten. Each
    // stored move is validated against the legal moves of the position - a
    // TT index collision could otherwise inject a corrupt move.
    private Move[] ExtractPv(Board board, Move firstMove, int maxLength)
    {
        bool IsLegal(Move move)
        {
            MoveGenerator.GenerateLegalMoves(board, _pvScratch);
            return _pvScratch.Contains(move);
        }

        var pv = new List<Move>(maxLength) { firstMove };
        board.MakeMove(firstMove);
        int made = 1;

        while (pv.Count < maxLength
               && _tt.Probe(board.ZobristKey, out TTEntry entry)
               && entry.BestMove != Move.None
               && IsLegal(entry.BestMove))
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
            || ElapsedMs >= _maxTimeMs
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
