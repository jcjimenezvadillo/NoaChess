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
// heuristic), which is a big part of its strength - but it also means a single
// instance must not run two searches CONCURRENTLY. Callers are responsible for
// finishing/cancelling one search before starting the next.
public sealed class ChessEngine
{
    public const string Version = "4.7.0";

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

    // ---- Persistent worker pool ----
    // The helpers used to be brand new OS threads created on EVERY search, which
    // made the thread count a fixed latency cost paid once per move BEFORE a
    // single node was searched. Measured on the Windows bot machine, delay until
    // "info depth 1":
    //
    //     Threads=1     1 ms      Threads=8    16 ms
    //     Threads=2     1 ms      Threads=24  480 ms
    //     Threads=4     2 ms
    //
    // It grew superlinearly, and in real 1+1 games it reached a median of
    // 2178 ms and a peak of 6864 ms, so the search was flagging before it began.
    // The bot had to run at 8 threads to stay alive.
    //
    // Now the threads are created once and PARK on a semaphore between searches.
    // Waking a parked thread is a kernel signal, not a thread creation, so the
    // per-move cost stops depending on the thread count.
    private Thread[] _pool = [];
    private SemaphoreSlim[] _go = [];
    private CountdownEvent? _done;
    private volatile bool _poolShutdown;

    // Per-search state handed to the workers. Written by the main thread before
    // the go signal is released and read by the worker after it, so the
    // semaphore is what publishes them - no other synchronisation is needed.
    private Board[] _workerBoards = [];
    private SearchResult[] _workerResults = [];
    private SearchLimits _workerLimits;
    private CancellationToken _workerToken;

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

        // Arrays are allocated once by EnsureHelpers and reused, so a search
        // allocates nothing here beyond the cloned boards.
        SearchResult[] results = _workerResults;
        for (int i = 0; i < n; i++)
        {
            results[i] = new SearchResult(Move.None, 0, 0);
            _workerBoards[i] = board.Clone();
        }

        // Helpers search until stopped (token), but respect an explicit depth
        // cap so "go depth N" does not run them unbounded.
        _workerLimits = limits.MaxDepth == SearchLimits.DepthUnlimited
            ? SearchLimits.Unlimited()
            : SearchLimits.Depth(limits.MaxDepth);
        _workerToken = linked.Token;

        // Wake the parked workers. Everything they read was written above, and
        // the semaphore release is the barrier that publishes it. _done counts
        // them back in after the main worker decides.
        _done!.Reset(n - 1);
        for (int i = 1; i < n; i++)
            _go[i - 1].Release();

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
            results[0] = _search.FindBestMove(_workerBoards[0], limits, linked.Token, progress, newSearch: false);
        }
        finally
        {
            _search.SearchThreadCount = 1;
            _search.PeerBestMoveChanges = null;
        }

        // Main decided (time/depth/nodes): stop the helpers and gather. The
        // workers are NOT joined - they park again for the next search - so this
        // waits on the countdown instead. Waiting is still mandatory: reading
        // their results while they are mid-search would race.
        linked.Cancel();
        _done.Wait();

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

        // The worker THREADS are rebuilt only when the count changes, not when
        // the searchers are replaced. A new game or a new evaluator swaps
        // _helpers, and the parked threads simply pick up the new instances on
        // their next wake-up - which is the whole point of keeping them alive.
        if (_pool.Length != need)
            RebuildPool(need);

        foreach (AlphaBetaSearch h in _helpers)
        {
            h.Profile = _search.Profile;
            h.SyzygyProbeLimit = _search.SyzygyProbeLimit;
            h.SyzygyProbeDepth = _search.SyzygyProbeDepth;
            h.Syzygy50MoveRule = _search.Syzygy50MoveRule;
        }
    }

    // Creates the worker threads for a helper count, retiring the previous ones
    // first. Called once at startup and then only if "Threads" changes, so
    // thread creation stops being a per-move cost.
    private void RebuildPool(int need)
    {
        ShutdownPool();

        // Slot 0 belongs to the main worker, so both arrays hold need + 1.
        _workerBoards = new Board[need + 1];
        _workerResults = new SearchResult[need + 1];
        if (need == 0)
            return;

        _poolShutdown = false;
        _go = new SemaphoreSlim[need];
        _pool = new Thread[need];
        // Seeded at 1 only so the countdown is valid before the first search;
        // every search resets it to the live worker count.
        _done = new CountdownEvent(1);
        for (int i = 0; i < need; i++)
        {
            // maxCount 1: a search releases exactly one permit per worker and
            // then waits for all of them, so a second outstanding permit would
            // mean the protocol had already been broken. Let it throw there
            // rather than silently run a worker twice on one position.
            _go[i] = new SemaphoreSlim(0, 1);
            int index = i;
            _pool[i] = new Thread(() => WorkerLoop(index))
            {
                IsBackground = true,
                Name = $"NoaSearch-{index + 1}",
            };
            _pool[i].Start();
        }
    }

    // Retires the current worker threads. Only ever called with no search in
    // flight (from EnsureHelpers, before a search starts), which is what makes
    // it safe to swap the arrays the workers index into afterwards.
    private void ShutdownPool()
    {
        if (_pool.Length == 0)
            return;

        _poolShutdown = true;
        foreach (SemaphoreSlim go in _go)
            go.Release();
        foreach (Thread t in _pool)
            t.Join();
        foreach (SemaphoreSlim go in _go)
            go.Dispose();
        _done?.Dispose();

        _pool = [];
        _go = [];
        _done = null;
    }

    // A parked worker: wait to be woken, search, report, park again. It reads
    // _helpers on every wake-up rather than capturing it, so replacing the
    // searchers (new game, new evaluator) needs no new threads.
    private void WorkerLoop(int index)
    {
        while (true)
        {
            _go[index].Wait();
            if (_poolShutdown)
                return;

            try
            {
                _workerResults[index + 1] = _helpers[index].FindBestMove(
                    _workerBoards[index + 1], _workerLimits, _workerToken, null, newSearch: false);
            }
            catch
            {
                // A helper that throws must not take the search down with it:
                // the main worker's line is what gets played. Report an empty
                // result, which the vote skips.
                _workerResults[index + 1] = new SearchResult(Move.None, 0, 0);
            }
            finally
            {
                // MUST run on every path. A missed signal hangs the engine
                // forever on the next _done.Wait().
                _done!.Signal();
            }
        }
    }

    // Move voting across the workers (score-weighted, decisive-aware). Each
    // worker contributes its last completed iteration's best move and score.
    private static SearchResult VoteBestResult(SearchResult[] results)
    {
        const int decisiveBound = AlphaBetaSearch.MateScore - 1000;

        // A HELPER MAY ONLY VOTE IF IT SEARCHED AT LEAST AS DEEP AS THE MAIN
        // WORKER. Scores from different depths are not comparable: a helper
        // still at depth 1 reports a far rosier number than a main worker at
        // depth 17, purely because it has not met the refutation yet, and the
        // weighting below then hands it the vote by a wide margin.
        //
        // This is not hypothetical. On 2026-08-09 the bot played 18...Nxe3 into
        // mate in one while every info line it printed reported the correct
        // move: the PV comes from the main worker, the move came from here. It
        // happened twice in four minutes once the helpers started fast enough to
        // return a depth-1 result at all. The deeper helpers still vote, which
        // is the whole point of the vote; the blind ones no longer do.
        int mainDepth = results[0].Depth;
        if (results[0].BestMove == Move.None)
            return results[0]; // no legal move: nothing to vote on

        static bool Eligible(SearchResult r, int minDepth)
            => r.BestMove != Move.None && r.Depth >= minDepth;

        int minScore = int.MaxValue;
        foreach (SearchResult r in results)
            if (Eligible(r, mainDepth) && r.Score < minScore)
                minScore = r.Score;

        var votes = new Dictionary<Move, long>();
        foreach (SearchResult r in results)
        {
            if (!Eligible(r, mainDepth))
                continue;
            votes.TryGetValue(r.BestMove, out long v);
            votes[r.BestMove] = v + (r.Score - minScore) + 14;
        }

        int bestIdx = 0; // worker 0 (main) always has a valid completed result
        for (int i = 1; i < results.Length; i++)
        {
            if (!Eligible(results[i], mainDepth))
                continue;

            SearchResult best = results[bestIdx];
            SearchResult cur = results[i];

            // DECISIVE MEANS WINNING, NOT "FAR FROM ZERO". These two tests
            // used Math.Abs, so a worker that had found a forced LOSS counted
            // as decisive - and `curDecisive` short-circuits the whole vote.
            // A single worker announcing "I am mated" therefore took the move
            // outright, no matter what the others had found; and once such a
            // result sat in bestIdx, the branch below preferred the largest
            // |score| among decisive ones, which for losses is the SHORTEST
            // mate. The engine picked the fastest way to be mated and then
            // printed the main worker's sane PV next to it.
            //
            // Being mated is not a claim worth acting on: every alternative is
            // at least as good. The `cur.Score > -decisiveBound` test below
            // already keeps mated workers out of the ordinary vote, so with
            // the sign restored a lost result can no longer win anything.
            bool bestDecisive = best.Score >= decisiveBound;
            bool curDecisive = cur.Score >= decisiveBound;

            if (bestDecisive)
            {
                // Among winning lines prefer the shortest mate: higher score.
                if (curDecisive && cur.Score > best.Score)
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
