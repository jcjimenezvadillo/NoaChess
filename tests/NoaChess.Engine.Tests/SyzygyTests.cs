using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine.Search;
using NoaChess.Engine.Tablebases;

namespace NoaChess.Engine.Tests;

internal static class SyzygyTestEnvironment
{
    private const string SyzygyPath = @"F:\Works\_______________CHESSTEST\syzygy";

    public static string? TablebasePath { get; } = FindPath();

    public static string? SkipReason => TablebasePath is null
        ? $"No Syzygy WDL/DTZ files found at {SyzygyPath}."
        : null;

    private static string? FindPath()
    {
        if (!Directory.Exists(SyzygyPath))
            return null;

        return Directory.EnumerateFiles(SyzygyPath, "*.rtbw").Any()
            ? Path.GetFullPath(SyzygyPath) : null;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SyzygyFactAttribute : FactAttribute
{
    public SyzygyFactAttribute() => Skip = SyzygyTestEnvironment.SkipReason;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SyzygyTheoryAttribute : TheoryAttribute
{
    public SyzygyTheoryAttribute() => Skip = SyzygyTestEnvironment.SkipReason;
}

// Syzygy probing is differentially tested: a wrong index does not throw, it
// returns a WRONG result that looks perfectly valid and the search then trusts
// it absolutely. These integration fixtures were checked against an independent
// prober and run only when the hardcoded syzygy path contains the large external data.
public class SyzygyIntegrationTests
{
    private static string TbPath => SyzygyTestEnvironment.TablebasePath!;

    private static void EnsureInit()
    {
        if (Syzygy.CurrentPath != TbPath)
            Syzygy.Init(TbPath);
    }

    [SyzygyFact]
    public void LoadsTablebasesAndReportsCardinality()
    {
        EnsureInit();
        Assert.True(Syzygy.Available);
        Assert.InRange(Syzygy.Cardinality, 3, 7);
    }

    // Every expectation below was DERIVED from an independent prober rather
    // than reasoned by hand — three hand-written fixtures in the first draft
    // were wrong (two were illegal positions with the side not to move already
    // in check, one missed that Rxh1 simply wins the rook).
    [SyzygyTheory]
    [InlineData("8/8/8/8/8/3k4/8/3KQ3 w - - 0 1", WdlScore.Win)]     // KQvK
    [InlineData("8/8/8/3k4/8/3K4/3B4/8 w - - 0 1", WdlScore.Draw)]   // KBvK, no mate possible
    [InlineData("8/8/8/3k4/8/3K4/3N4/8 w - - 0 1", WdlScore.Draw)]   // KNvK, no mate possible
    [InlineData("8/8/8/3k4/8/3K4/3R4/8 w - - 0 1", WdlScore.Win)]    // KRvK
    [InlineData("4k3/8/4K3/8/8/8/8/4R3 w - - 0 1", WdlScore.Win)]    // KRvK, king cut off
    [InlineData("8/8/4k3/8/8/4K3/8/R6r w - - 0 1", WdlScore.Win)]    // Rxh1 wins the rook
    [InlineData("8/8/8/8/8/8/1p6/1k1K4 w - - 0 1", WdlScore.Loss)]   // KvKP, the pawn queens
    public void ProbeWdl_MatchesKnownOutcomes(string fen, WdlScore expected)
    {
        EnsureInit();
        var board = new Board(fen);
        Assert.True(Syzygy.ProbeWdl(board, out WdlScore score), "position should be in the TBs");
        Assert.Equal(expected, score);
    }

    [SyzygyTheory]
    [InlineData("8/8/8/8/8/3k4/8/3KQ3 w - - 0 1", 9)]      // KQvK
    [InlineData("8/8/8/3k4/8/3K4/3R4/8 w - - 0 1", 23)]    // KRvK
    [InlineData("4k3/8/4K3/8/8/8/8/4R3 w - - 0 1", 5)]     // KRvK, king cut off
    [InlineData("8/8/4k3/8/8/4K3/8/R6r w - - 0 1", 1)]     // Rxh1 zeroes immediately
    [InlineData("8/8/8/3k4/8/3K4/3B4/8 w - - 0 1", 0)]     // Draw: no distance to store
    [InlineData("8/8/8/8/8/8/1p6/1k1K4 w - - 0 1", -4)]    // Lost: negative distance
    public void ProbeDtz_MatchesKnownDistances(string fen, int expected)
    {
        EnsureInit();
        var board = new Board(fen);
        Assert.True(Syzygy.ProbeDtz(board, out int dtz), "position should be in the TBs");
        Assert.Equal(expected, dtz);
    }

    [SyzygyFact]
    public void ProbeWdl_RejectsPositionsOutsideTheTablebases()
    {
        EnsureInit();
        // Opening position: far more men than any 5-piece table covers.
        var board = new Board();
        Assert.False(Syzygy.ProbeWdl(board, out _));
    }

    [SyzygyFact]
    public void ProbeWdl_RejectsPositionsWithCastlingRights()
    {
        EnsureInit();
        // The tables know nothing about castling, so such a position must be
        // refused rather than answered from the wrong entry.
        var board = new Board("4k2r/8/8/8/8/8/8/4K3 b k - 0 1");
        Assert.False(Syzygy.ProbeWdl(board, out _));
    }

    private static ChessEngine NewTbEngine()
    {
        EnsureInit();
        var engine = new ChessEngine();
        engine.SyzygyProbeLimit = 7; // Refreshes the cached loaded cardinality.
        return engine;
    }

    [SyzygyFact]
    public void SearchRoot_DoesNotRegenerateMovesAfterTablebaseFiltering()
    {
        var engine = NewTbEngine();
        var board = new Board("8/1B6/2qk4/2q5/8/8/8/4K3 b - - 30 1");

        var result = engine.FindBestMove(board, depth: 1);

        Assert.Equal("c6b7", result.BestMove.ToString());
    }

    [SyzygyFact]
    public void RootDtz_ZeroingPromotionPreservesWinAtRule50Boundary()
    {
        var engine = NewTbEngine();
        var board = new Board("7k/2P5/7p/8/3K4/8/8/8 w - - 99 1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = engine.FindBestMove(board, depth: 6, cts.Token);

        Assert.Contains(result.BestMove.ToString(), new[] { "c7c8q", "c7c8r" });
    }

    [SyzygyFact]
    public void RootDtz_LostPositionChoosesLongestDefense()
    {
        var engine = NewTbEngine();
        var board = new Board("8/8/8/k6K/8/8/6R1/8 b - - 0 1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = engine.FindBestMove(board, depth: 6, cts.Token);

        Assert.Contains(result.BestMove.ToString(), new[] { "a5b4", "a5b5" });
    }

    [SyzygyFact]
    public void CancelledFallback_StaysInsideTablebaseFilteredMoves()
    {
        var engine = NewTbEngine();
        var board = new Board("7K/8/6k1/q7/8/8/8/8 b - - 80 1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = engine.FindBestMove(board, depth: 6, cts.Token);

        Assert.Contains(result.BestMove.ToString(), new[] { "a5a8", "a5d8" });
    }

    [SyzygyFact]
    public void TbFilteredSingleton_IsStillSearchedWhenOtherLegalMovesExist()
    {
        var engine = NewTbEngine();
        var board = new Board("8/8/8/8/8/3k4/8/3KQ3 w - - 0 1");
        var limits = new SearchLimits(MaxDepth: 1, HardTimeMs: 10_000,
                                      SoftTimeMs: 9_000, MaxNodes: long.MaxValue);

        var result = engine.FindBestMove(board, limits);

        Assert.Equal("e1e5", result.BestMove.ToString());
        Assert.True(result.NodesSearched > 0,
            "TB filtering must not turn several legal moves into the forced-move early return");
    }

    [SyzygyFact]
    public void RootFiltering_FallsBackToWdlWhenDtzIsMissing()
    {
        string subset = CreateSubset("KQvK.rtbw");
        try
        {
            Syzygy.Init(subset);
            var engine = new ChessEngine { SyzygyProbeLimit = 7 };
            var board = new Board("7k/8/5KQ1/8/8/8/8/8 w - - 0 1");

            // This position has both winning continuations and stalemates. The
            // WDL-only subset must therefore do real filtering, not merely
            // report a successful no-op after DTZ failed to load.
            var legal = new MoveList();
            MoveGenerator.GenerateLegalMoves(board, legal);
            var outcomes = new HashSet<WdlScore>();
            for (int i = 0; i < legal.Count; i++)
            {
                board.MakeMove(legal[i]);
                Assert.True(Syzygy.ProbeWdl(board, out WdlScore child));
                outcomes.Add((WdlScore)(-(int)child));
                board.UnmakeMove();
            }
            Assert.Contains(WdlScore.Draw, outcomes);
            Assert.Contains(WdlScore.Win, outcomes);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var result = engine.FindBestMove(board, depth: 6, cts.Token);

            Assert.True(engine.TbHits > 0, "the WDL root fallback should count as a TB hit");
            board.MakeMove(result.BestMove);
            Assert.True(Syzygy.ProbeWdl(board, out WdlScore chosenChild));
            Assert.Equal(WdlScore.Loss, chosenChild);
            board.UnmakeMove();
        }
        finally
        {
            // Re-initialising must close the memory map so the temporary table
            // can be deleted immediately on Windows.
            Syzygy.Init(TbPath);
            Directory.Delete(subset, recursive: true);
        }
    }

    [SyzygyFact]
    public void ProbeDepth_IsIgnoredBelowTheConfiguredCardinality()
    {
        string subset = CreateSubset("KRBvKN.rtbw");
        try
        {
            Syzygy.Init(subset);                 // Loaded cardinality: five
            var engine = new ChessEngine
            {
                SyzygyProbeLimit = 7,            // Configured cardinality: seven
                SyzygyProbeDepth = 100
            };

            // The six-man root is outside the installed subset. Rxa8 reaches a
            // five-man position with no castling rights, which Stockfish probes
            // at depth zero because five is below the configured limit of seven.
            var board = new Board("r3k3/6n1/8/8/8/8/1B6/R3K3 w Q - 0 1");
            engine.FindBestMove(board, depth: 1);

            Assert.True(engine.TbHits > 0,
                "installed sub-cardinality tables should ignore SyzygyProbeDepth");
        }
        finally
        {
            Syzygy.Init(TbPath);
            Directory.Delete(subset, recursive: true);
        }
    }

    private static string CreateSubset(params string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            string source = Path.Combine(TbPath, fileName);
            Assert.True(File.Exists(source), $"Required integration table is missing: {source}");
        }

        string directory = Path.Combine(Path.GetTempPath(),
            $"NoaChess-Syzygy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        foreach (string fileName in fileNames)
        {
            string source = Path.Combine(TbPath, fileName);
            File.Copy(source, Path.Combine(directory, fileName));
        }

        return directory;
    }
}

// These checks need no tablebase files and therefore always run in CI.
public class SyzygyScoreTests
{
    [Fact]
    public void TablebaseScores_AreNodeRelativeAndRule50SafeInTheTt()
    {
        const int storedPly = 7;
        int storedWin = InvokePrivate<int>("ToTT", AlphaBetaSearch.TbWin - storedPly, storedPly);
        int storedLoss = InvokePrivate<int>("ToTT", -AlphaBetaSearch.TbWin + storedPly, storedPly);

        Assert.Equal(AlphaBetaSearch.TbWin, storedWin);
        Assert.Equal(-AlphaBetaSearch.TbWin, storedLoss);
        Assert.Equal(AlphaBetaSearch.TbWin - 3, InvokePrivate<int>("FromTT", storedWin, 3));
        Assert.Equal(-AlphaBetaSearch.TbWin + 3, InvokePrivate<int>("FromTT", storedLoss, 3));
        Assert.True(InvokePrivate<bool>("CanReuseTtScore", storedWin, 0));
        Assert.False(InvokePrivate<bool>("CanReuseTtScore", storedWin, 1));
        Assert.True(InvokePrivate<bool>("CanReuseTtScore", 500, 99));
    }

    private static T InvokePrivate<T>(string name, params object[] arguments)
    {
        MethodInfo? method = typeof(AlphaBetaSearch).GetMethod(
            name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (T)method.Invoke(null, arguments)!;
    }
}
