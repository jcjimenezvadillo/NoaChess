using NoaChess.Core;
using NoaChess.Engine;

namespace NoaChess.Engine.Tests;

// Core-Engine integration tests: the engine must always return legal moves
// and find elementary tactics at low depth.
public class SearchTests
{
    private readonly ChessEngine _engine = new();

    [Fact]
    public void FindBestMove_ReturnsLegalMove_FromStartPosition()
    {
        var board = new Board();
        var result = _engine.FindBestMove(board, depth: 3);

        // The returned move must be among the Core's legal ones.
        Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
    }

    [Fact]
    public void FindsMateInOne()
    {
        // White delivers a back-rank mate with Ra8#.
        var board = new Board("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 3);
        Assert.Equal("a1a8", result.BestMove.ToString());
    }

    [Fact]
    public void CapturesHangingQueen()
    {
        // The black queen on d5 is hanging; the white rook on d1 must capture it.
        var board = new Board("4k3/8/8/3q4/8/8/8/3RK3 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 3);
        Assert.Equal("d1d5", result.BestMove.ToString());
    }

    [Fact]
    public void IterativeDeepening_ReportsProgressPerDepth()
    {
        // The search must report one snapshot per completed depth, in order,
        // with a legal best move in each one.
        var board = new Board();
        var reported = new List<Engine.Search.SearchProgress>();
        var progress = new InlineProgress(reported.Add);

        _engine.FindBestMove(board, depth: 3, progress: progress);

        Assert.Equal([1, 2, 3], reported.Select(p => p.Depth));
        var legalMoves = MoveGenerator.GenerateLegalMoves(board);
        Assert.All(reported, p => Assert.Contains(p.BestMove, legalMoves));
    }

    // IProgress<T> that invokes the callback synchronously, so the test sees
    // all reports before FindBestMove returns (Progress<T> posts to a
    // SynchronizationContext and would race with the assertions).
    private sealed class InlineProgress(Action<Engine.Search.SearchProgress> callback)
        : IProgress<Engine.Search.SearchProgress>
    {
        public void Report(Engine.Search.SearchProgress value) => callback(value);
    }

    [Fact]
    public void FindsMateInTwo()
    {
        // Classic two-rook ladder: 1.Ra7 (confining the king to the 8th rank)
        // followed by 2.Rb8#. Requires seeing 3 plies ahead plus mate detection.
        var board = new Board("7k/8/8/8/8/8/R7/1R5K w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 4);

        // The score must be a mate score (near MateScore), not a material eval.
        Assert.True(result.Score > Engine.Search.AlphaBetaSearch.MateScore - 100,
            $"Expected a mate score, got {result.Score}");
    }

    [Fact]
    public void Quiescence_AvoidsHorizonBlunder()
    {
        // The black d5 pawn is defended by the e6 pawn. At depth 1 a searcher
        // WITHOUT quiescence evaluates right after Qxd5 (up a pawn) and never
        // sees ...exd5 losing the queen. Quiescence must reject the capture.
        var board = new Board("4k3/8/4p3/3p4/8/8/8/3QK3 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 1);

        Assert.NotEqual("d1d5", result.BestMove.ToString());
    }

    [Fact]
    public void TimeLimit_IsRespected()
    {
        // A time-limited search must come back promptly (well under the huge
        // depth cap) and still produce a legal move.
        var board = new Board();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var result = _engine.FindBestMove(board, Engine.Search.SearchLimits.Time(200));
        stopwatch.Stop();

        Assert.True(stopwatch.ElapsedMilliseconds < 2_000,
            $"Search took {stopwatch.ElapsedMilliseconds} ms for a 200 ms budget");
        Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
    }

    [Fact]
    public void SearchRespectsCancellation()
    {
        // With an already cancelled token the search must still return quickly
        // with a valid move (the best one seen so far).
        var board = new Board();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = _engine.FindBestMove(board, depth: 6, cts.Token);
        Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
    }

    [Fact]
    public void CancelledBeforeDepthOne_FallsBackToStaticBestNotFirstMove()
    {
        // A search cancelled before it can complete even depth 1 (e.g. a cold
        // process on a tiny first-move budget) must not return the FIRST
        // generated move — move ordering makes that a rook-pawn push (…a6),
        // which looks absurd. The fallback picks the static-best move instead.
        // After 1.e4 the static eval prefers …d5 over …a6 by a wide margin.
        var board = new Board("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = _engine.FindBestMove(board, depth: 6, cts.Token);

        Assert.NotEqual("a7a6", result.BestMove.ToString());
        Assert.Equal("d7d5", result.BestMove.ToString());
    }

    [Fact]
    public void EngineVsEngine_PlaysLegalGame()
    {
        // Mini integration test: the engine plays against itself for a few
        // moves on the SAME Board (just like the GUI does) without producing
        // illegal states.
        var board = new Board();
        for (int i = 0; i < 10 && GameState.GetResult(board) == GameResult.Ongoing; i++)
        {
            var result = _engine.FindBestMove(board, depth: 2);
            Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
            board.MakeMove(result.BestMove);
        }
    }
}
