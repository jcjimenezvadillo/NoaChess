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

    // Active parameter profile (Default/Bullet, see EngineProfile).
    /// Syzygy probing settings, driven by the UCI options of the same name.
    public int SyzygyProbeLimit { set => _search.SyzygyProbeLimit = value; }
    public int SyzygyProbeDepth { set => _search.SyzygyProbeDepth = value; }
    public bool Syzygy50MoveRule { set => _search.Syzygy50MoveRule = value; }

    /// Positions this search resolved from tablebases (UCI "tbhits").
    public long TbHits => _search.TbHits;

    /// Must be called after the tablebases are (re)loaded: the search caches
    /// the largest piece count worth probing.
    public void RefreshTablebaseLimit() => _search.RefreshTbLimit();

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

    // Loads a .noannue model. On success the evaluator can be switched with
    // SetUseNnue; on failure the classical evaluator stays active and the
    // error explains why (the UCI host forwards it as "info string").
    public bool TryLoadNnueModel(string path, out string error)
    {
        if (!NnueModelLoader.TryLoad(path, out NnueNetwork? network, out error))
            return false;

        _nnue = new NnueEvaluator(network!);
        if (NnueActive)
            _search.SetEvaluator(_nnue); // Refresh active instance.
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

        // JIT warm-up: the first calls into NNUE's SIMD inference path and
        // the accumulator update code trigger tiered-compilation recompiles
        // (Tier0 -> Tier1) the first few dozen times they run. Left alone,
        // that recompilation happens whenever it happens — possibly deep
        // into a real timed game, where a stall eats into the clock. Doing a
        // short, throwaway search right here (setoption time, before any
        // clock is running) pays that one-time cost up front instead.
        if (useNnue)
            _search.FindBestMove(new Board(), SearchLimits.Depth(6));

        return true;
    }
}
