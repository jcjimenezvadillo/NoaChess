using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Evaluation.Nnue;
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
    public const string Version = "4.0.0";

    private readonly AlphaBetaSearch _search = new(new ClassicalEvaluator());

    // ---- Lazy SMP parallel search ----
    // Extra worker threads search the SAME root position on their own board and
    // heuristics but SHARE the main worker's transposition table; they diverge
    // through TT races and cross-pollinate the best lines. At the end the
    // workers vote on the move. Threads=1 keeps the exact single-threaded path.
    public const int MaxThreads = 32;
    private int _threads = 1;
    private AlphaBetaSearch[] _helpers = [];
    private bool _helpersStale = true;

    // UCI "Threads": number of parallel search threads (1 = single-threaded).
    public int Threads
    {
        get => _threads;
        set
        {
            int v = Math.Clamp(value, 1, MaxThreads);
            if (v == _threads)
                return;
            _threads = v;
            _helpersStale = true; // pool size changed: rebuild before next search
        }
    }

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
        => _threads <= 1
            ? _search.FindBestMove(board, limits, cancellation, progress)
            : FindBestMoveParallel(board, limits, cancellation, progress);

    // Lazy SMP search: main worker (this thread) plus Threads-1 helpers on
    // dedicated threads, all sharing one transposition table. The main worker
    // owns time management; when it returns, the helpers are stopped and the
    // workers vote on the best move.
    private SearchResult FindBestMoveParallel(Board board, SearchLimits limits,
                                              CancellationToken cancellation,
                                              IProgress<SearchProgress>? progress)
    {
        EnsureHelpers();
        int n = _threads;

        // Age the shared table exactly ONCE; every worker then runs with
        // newSearch:false so the shared generation is not bumped n times.
        _search.NewSearchTt();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var results = new SearchResult[n];
        var boards = new Board[n];
        for (int i = 0; i < n; i++)
            boards[i] = board.Clone();

        // Helpers search until stopped (token), but respect an explicit depth
        // cap so "go depth N" does not run them unbounded.
        SearchLimits helperLimits = limits.MaxDepth == SearchLimits.DepthUnlimited
            ? SearchLimits.Unlimited()
            : SearchLimits.Depth(limits.MaxDepth);

        var helperThreads = new Thread[n - 1];
        for (int i = 1; i < n; i++)
        {
            int idx = i;
            AlphaBetaSearch worker = _helpers[idx - 1];
            Board wb = boards[idx];
            CancellationToken wt = linked.Token;
            var t = new Thread(() =>
                results[idx] = worker.FindBestMove(wb, helperLimits, wt, null, newSearch: false))
            {
                IsBackground = true,
                Name = $"NoaSearch-{idx}",
            };
            helperThreads[idx - 1] = t;
            t.Start();
        }

        // Couple the pool to the main worker's time manager: its instability
        // factor averages root best-move changes over ALL workers (peer sum /
        // thread count), which keeps it low-variance instead of letting one
        // thread's noisy count spike the budget. Reset afterwards so the
        // single-threaded path (which uses _search directly) is never affected.
        AlphaBetaSearch[] pool = _helpers;
        int lastPeerTotal = 0; // fresh per search; peer func runs only on the main thread
        _search.SearchThreadCount = n;
        _search.PeerBestMoveChanges = () =>
        {
            int cur = 0;
            for (int i = 0; i < pool.Length; i++)
                cur += pool[i].BestMoveChangesTotal;
            int delta = cur - lastPeerTotal; // changes since the last main iteration
            lastPeerTotal = cur;
            return delta;
        };

        // The main worker runs on the calling thread so its "info" progress is
        // reported from where the UCI host expects it.
        try
        {
            results[0] = _search.FindBestMove(boards[0], limits, linked.Token, progress, newSearch: false);
        }
        finally
        {
            _search.SearchThreadCount = 1;
            _search.PeerBestMoveChanges = null;
        }

        // Main decided (time/depth/nodes): stop the helpers and gather.
        linked.Cancel();
        foreach (Thread t in helperThreads)
            t.Join();

        long totalNodes = 0;
        foreach (SearchResult r in results)
            totalNodes += r.NodesSearched;

        // Depth-limited / analysis searches take the main thread's line as-is;
        // time-controlled play votes across the threads for robustness.
        SearchResult chosen = limits.MaxDepth != SearchLimits.DepthUnlimited
            ? results[0]
            : VoteBestResult(results);

        return chosen with { NodesSearched = totalNodes };
    }

    // Rebuilds the helper pool when the thread count or evaluator changed, then
    // re-syncs the tunable settings onto every helper.
    private void EnsureHelpers()
    {
        int need = _threads - 1;
        if (_helpersStale || _helpers.Length != need)
        {
            var pool = new AlphaBetaSearch[need];
            for (int i = 0; i < need; i++)
                pool[i] = new AlphaBetaSearch(_search.Evaluator.Clone(), _search.Tt);
            _helpers = pool;
            _helpersStale = false;
        }

        foreach (AlphaBetaSearch h in _helpers)
        {
            h.Profile = _search.Profile;
            h.SyzygyProbeLimit = _search.SyzygyProbeLimit;
            h.SyzygyProbeDepth = _search.SyzygyProbeDepth;
            h.Syzygy50MoveRule = _search.Syzygy50MoveRule;
        }
    }

    // Move voting across the workers (score-weighted, decisive-aware). Each
    // worker contributes its last completed iteration's best move and score.
    private static SearchResult VoteBestResult(SearchResult[] results)
    {
        const int decisiveBound = AlphaBetaSearch.MateScore - 1000;

        int minScore = int.MaxValue;
        foreach (SearchResult r in results)
            if (r.BestMove != Move.None && r.Score < minScore)
                minScore = r.Score;

        var votes = new Dictionary<Move, long>();
        foreach (SearchResult r in results)
        {
            if (r.BestMove == Move.None)
                continue;
            votes.TryGetValue(r.BestMove, out long v);
            votes[r.BestMove] = v + (r.Score - minScore) + 14;
        }

        int bestIdx = 0; // worker 0 (main) always has a valid completed result
        for (int i = 1; i < results.Length; i++)
        {
            if (results[i].BestMove == Move.None)
                continue;

            SearchResult best = results[bestIdx];
            SearchResult cur = results[i];
            bool bestDecisive = Math.Abs(best.Score) >= decisiveBound;
            bool curDecisive = Math.Abs(cur.Score) >= decisiveBound;

            if (bestDecisive)
            {
                // Among decisive lines prefer the shortest (largest |score|).
                if (curDecisive && Math.Abs(cur.Score) > Math.Abs(best.Score))
                    bestIdx = i;
            }
            else if (curDecisive
                     || (cur.Score > -decisiveBound
                         && votes[cur.BestMove] > votes[best.BestMove]))
            {
                bestIdx = i;
            }
        }
        return results[bestIdx];
    }

    // Convenience overload: fixed-depth search (DefaultDepth when omitted).
    public SearchResult FindBestMove(Board board, int? depth = null,
                                     CancellationToken cancellation = default,
                                     IProgress<SearchProgress>? progress = null)
        => FindBestMove(board, SearchLimits.Depth(depth ?? DefaultDepth), cancellation, progress);

    // Forgets everything learned in the current game (transposition table,
    // heuristics). Call it when a NEW game starts ("ucinewgame").
    public void NewGame()
    {
        _search.Reset();
        _helpersStale = true; // rebuild helpers fresh (empty history) next search
    }

    // Reallocates the transposition table ("setoption name Hash value N").
    public void ResizeHash(int sizeMb) => _search.ResizeTT(sizeMb);

    // Syzygy probing settings, driven by the UCI options of the same name.
    public int SyzygyProbeLimit { set => _search.SyzygyProbeLimit = value; }
    public int SyzygyProbeDepth { set => _search.SyzygyProbeDepth = value; }
    public bool Syzygy50MoveRule { set => _search.Syzygy50MoveRule = value; }

    // Positions this search resolved from tablebases (UCI "tbhits").
    public long TbHits => _search.TbHits;

    // Must be called after the tablebases are (re)loaded: the search caches
    // the largest piece count worth probing.
    public void RefreshTablebaseLimit() => _search.RefreshTbLimit();

    // Active parameter profile (Default/Bullet, see EngineProfile).
    public Profiles.EngineProfile Profile
    {
        get => _search.Profile;
        set => _search.Profile = value;
    }

    // ---- Evaluator selection (Classical / NNUE) ----

    private NnueEvaluator? _nnue;

    // True while the NNUE evaluator is the active one.
    public bool NnueActive { get; private set; }

    // SHA-256 of the loaded model (reproducibility logging), or null.
    public string? NnueModelSha256 => _nnue?.ModelSha256;

    // Loaded network weights, or null when no model is loaded. Exposed for the
    // `nnueprofile` command, which times the inference primitives directly.
    public NnueNetwork? NnueNetwork => _nnue?.Network;

    // Loads a .noannue model. On success the evaluator can be switched with
    // SetUseNnue; on failure the classical evaluator stays active and the
    // error explains why (the UCI host forwards it as "info string").
    public bool TryLoadNnueModel(string path, out string error)
    {
        if (!NnueModelLoader.TryLoad(path, out NnueNetwork? network, out error))
            return false;

        _nnue = new NnueEvaluator(network!);
        if (NnueActive)
        {
            _search.SetEvaluator(_nnue); // Refresh active instance.
            _helpersStale = true;        // helpers must clone the new evaluator
        }
        return true;
    }

    public bool TryLoadNnueModel(ReadOnlySpan<byte> bytes, out string error)
    {
        if (!NnueModelLoader.TryParse(bytes, out NnueNetwork? network, out error))
            return false;

        _nnue = new NnueEvaluator(network!);
        if (NnueActive)
        {
            _search.SetEvaluator(_nnue);
            _helpersStale = true;
        }
        return true;
    }

    // Switches between the classical evaluator and the loaded NNUE model.
    // Returns false when NNUE is requested but no model is loaded.
    public bool SetUseNnue(bool useNnue)
    {
        if (useNnue && _nnue is null)
            return false;

        NnueActive = useNnue;
        _search.SetEvaluator(useNnue ? _nnue! : new ClassicalEvaluator());
        _helpersStale = true; // helpers must clone the newly active evaluator

        // NNUE accumulator warm-up: a depth-1 search initialises the lazy
        // accumulator for the start position and exercises the SIMD inference
        // path so any residual JIT cost is paid before the clock starts.
        // (Depth 6 was the original value; with PublishReadyToRun the native
        // code is already compiled at publish time so depth 1 is enough.)
        if (useNnue)
            _search.FindBestMove(new Board(), SearchLimits.Depth(1));

        return true;
    }
}
