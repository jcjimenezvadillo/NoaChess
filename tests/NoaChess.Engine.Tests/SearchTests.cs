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
