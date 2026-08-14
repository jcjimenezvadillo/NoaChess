using System.IO;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.GUI.Wpf.Services;

// Owns the engine instance and serialises access to it.
//
// A ChessEngine keeps state between searches (transposition table, history
// heuristics) and must never run two searches at once. Every entry point here
// therefore cancels AND AWAITS whatever was running before starting anything
// new, which is the one rule the whole GUI depends on for correctness.
//
// Searches always run on a CLONE of the position: the search makes and unmakes
// moves on the board it is given, and the UI thread is painting the real one
// at the same time.
public sealed class EngineService : IDisposable
{
    private readonly ChessEngine _engine = new();
    private CancellationTokenSource? _cancellation;
    private Task? _running;

    // Only one search may be inside the engine at a time. A plain
    // "cancel the old one, then start mine" is NOT enough: cancelling awaits,
    // and three requests arriving before the first await resumes all see an
    // idle engine, so two of them go on to search the same instance at once.
    // That is not theoretical - it crashed with an index out of range deep
    // inside the move loop the first time the GUI was driven fast.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Requests are numbered so that one which is overtaken while it waits for
    // the gate gives up instead of running a search nobody is waiting for any
    // more. Without it, a burst of moves would be searched one after another,
    // each at full depth, long after the position had moved on.
    private long _generation;

    // Model file currently loaded, for display. Null while the engine is on
    // the classical evaluator.
    public string? NnueModelName { get; private set; }

    public bool NnueActive => _engine.NnueActive;

    // Name shown in the status bar: which evaluator is actually deciding moves.
    public string EvaluatorName => NnueActive ? $"NNUE {NnueModelName}" : "Classical eval";

    // Depth ceiling for the analysis that runs while the human thinks. Without
    // it the analysis would search the same position forever and hold a core
    // busy for nothing once the line has long since converged.
    public int AnalysisMaxDepth { get; set; } = 32;

    public int Threads
    {
        get => _engine.Threads;
        private set => _engine.Threads = value;
    }

    private int _hashMb = 128;

    public int HashMb => _hashMb;

    // Both of these replace state a search reads, so they go through the gate.
    // The thread count only takes effect on the next search, but the hash table
    // is reallocated on the spot.
    public Task SetThreadsAsync(int threads) => MutateAsync(() => Threads = threads);

    public Task SetHashAsync(int megabytes) => MutateAsync(() =>
    {
        _hashMb = Math.Clamp(megabytes, 1, 4096);
        _engine.ResizeHash(_hashMb);
    });

    // Construction is the one moment there is no search to collide with, so the
    // engine is set up directly here rather than through the gate.
    public EngineService(int hashMb, int threads)
    {
        _hashMb = Math.Clamp(hashMb, 1, 4096);
        _engine.ResizeHash(_hashMb);
        _engine.Threads = threads;
        AutoLoadNnue();
    }

    // Looks for a network next to the executable and then up the tree in
    // models/nnue, so a development build finds the repository's own net and a
    // published folder finds whatever was copied beside the exe.
    public string? AutoLoadNnue()
    {
        foreach (string candidate in ProbeNnuePaths())
        {
            if (TryLoadNnue(candidate, out _))
                return candidate;
        }
        return null;
    }

    private static IEnumerable<string> ProbeNnuePaths()
    {
        string baseDir = AppContext.BaseDirectory;

        // Anything sitting beside the executable wins: that is what a published
        // folder looks like.
        if (Directory.Exists(baseDir))
        {
            foreach (string f in Directory.EnumerateFiles(baseDir, "*.noannue"))
                yield return f;
        }

        // Development build: climb out of bin/Debug/... until models/nnue shows up.
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string models = Path.Combine(dir.FullName, "models", "nnue");
            if (!Directory.Exists(models))
                continue;

            // The current best net first, then any other as a fallback.
            string best = Path.Combine(models, "noa-fq60.noannue");
            if (File.Exists(best))
                yield return best;
            foreach (string f in Directory.EnumerateFiles(models, "*.noannue"))
                yield return f;
            yield break;
        }
    }

    // Loads a .noannue model and switches the engine onto it. On failure the
    // classical evaluator stays active and 'error' says why.
    //
    // Private: every caller after construction must come through
    // LoadNnueAsync, which holds the gate. During construction there is no
    // search to collide with.
    private bool TryLoadNnue(string path, out string error)
    {
        if (!_engine.TryLoadNnueModel(path, out error))
            return false;
        if (!_engine.SetUseNnue(true))
        {
            error = "The model loaded but the evaluator refused to switch.";
            return false;
        }
        NnueModelName = Path.GetFileNameWithoutExtension(path);
        return true;
    }

    // Loads a network with no search in flight.
    public async Task<(bool Ok, string Error)> LoadNnueAsync(string path)
    {
        bool ok = false;
        string error = "";
        await MutateAsync(() => ok = TryLoadNnue(path, out error));
        return (ok, error);
    }

    // Forgets the transposition table and heuristics of the finished game.
    public Task NewGameAsync() => MutateAsync(_engine.NewGame);

    // ---- Endgame tablebases ----

    // Syzygy tables turn the last few pieces from a guess into a fact, which is
    // the difference between an endgame analysis being useful and being
    // decorative. Loading them replaces a static registry the search reads, so
    // it goes through the gate like everything else.
    public Task LoadTablebasesAsync(string path) => MutateAsync(() =>
    {
        NoaChess.Engine.Tablebases.Syzygy.Init(path);
        _engine.RefreshTablebaseLimit();
    });

    public bool TablebasesAvailable => NoaChess.Engine.Tablebases.Syzygy.Available;

    // "Syzygy 5-man" or nothing at all, for the status line.
    public string TablebaseDescription =>
        NoaChess.Engine.Tablebases.Syzygy.Available
            ? $"Syzygy {NoaChess.Engine.Tablebases.Syzygy.Cardinality}-man"
            : "";

    // Asks the running search to stop WITHOUT waiting for it. The search
    // answers a stop with the best move it has found so far, so this is what
    // "move now" is made of: the caller stays in its await and receives a real
    // move instead of an abandoned search.
    public void RequestStop() => _cancellation?.Cancel();

    // Runs something that CHANGES the engine, with the guarantee that no search
    // is inside it. Loading a network, resetting for a new game and resizing
    // the hash table all replace state a running search is reading, and doing
    // any of them under one crashes: swapping the evaluator mid-search took the
    // NNUE accumulator stack out of bounds the first time this was tried.
    //
    // Stopping first is NOT enough on its own. Stopping is an await, and by the
    // time it returns another continuation may already have started the next
    // search, so the mutation has to hold the same gate the searches queue on.
    public async Task MutateAsync(Action change)
    {
        Interlocked.Increment(ref _generation);
        _cancellation?.Cancel();
        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            change();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Cancels the running search and waits until it has really finished, and
    // until nothing else is queued behind it either.
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation); // supersede anything waiting
        _cancellation?.Cancel();

        // Taking the gate is what proves the engine is idle: whoever holds it
        // only lets go once its search has returned.
        await _gate.WaitAsync().ConfigureAwait(true);
        _gate.Release();
        _running = null;
    }

    // Runs one search to completion and returns its result. The caller decides
    // the limits: a timed budget for the engine's own move, a depth ceiling for
    // the analysis that runs on the human's clock.
    public async Task<SearchResult> SearchAsync(Board position, SearchLimits limits,
                                                IProgress<SearchProgress>? progress,
                                                CancellationToken external = default)
    {
        long mine = Interlocked.Increment(ref _generation);

        // Cancel first, queue second: the search that currently holds the gate
        // has to be told to wind down before there is any point waiting for it.
        _cancellation?.Cancel();
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            // Overtaken while queueing: the position this was about is gone.
            if (Interlocked.Read(ref _generation) != mine)
                return new SearchResult(Move.None, 0, 0);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _cancellation = cts;
            CancellationToken token = cts.Token;

            // The token is not handed to Task.Run: a cancelled search returns
            // the best move it had rather than throwing, and that result is
            // exactly what "move now" needs to receive.
            Board copy = position.Clone();
            var task = Task.Run(() => _engine.FindBestMove(copy, limits, token, progress));
            _running = task;

            try
            {
                return await task.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return new SearchResult(Move.None, 0, 0);
            }
            finally
            {
                _running = null;
                _cancellation = null;
                cts.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The engine's own move, under whatever limit the time control implies.
    public Task<SearchResult> PlayMoveAsync(Board position, SearchLimits limits,
                                            IProgress<SearchProgress>? progress)
        => SearchAsync(position, limits, progress);

    // Depth-capped analysis of the position on screen, cancelled as soon as
    // that position changes.
    public Task<SearchResult> AnalyseAsync(Board position, IProgress<SearchProgress>? progress)
        => SearchAsync(position, SearchLimits.Depth(AnalysisMaxDepth), progress);

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _cancellation?.Cancel();
        try { _running?.Wait(2000); }
        catch { /* shutting down: a stuck search must not block the exit */ }

        // The gate is deliberately NOT disposed. Closing the window while the
        // idle analysis is running - which is most of the time in analysis mode
        // - leaves that search waiting on it, and the search still has to run
        // its finally and release. Disposing here turned an ordinary close into
        // an ObjectDisposedException on a thread nobody is catching. A
        // SemaphoreSlim that was never waited on as a handle holds nothing
        // worth reclaiming.
    }
}
