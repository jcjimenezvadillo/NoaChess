using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Game-over detection tests (mate, stalemate, fifty-move rule).
public class GameStateTests
{
    [Fact]
    public void ScholarsMate_IsCheckmate()
    {
        // Completed scholar's mate: it is black's turn and they are mated.
        var board = new Board("r1bqkb1r/pppp1Qpp/2n2n2/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 0 4");
        Assert.Equal(GameResult.Checkmate, GameState.GetResult(board));
        Assert.True(board.IsInCheck());
    }

    [Fact]
    public void CorneredKing_IsStalemate()
    {
        // Black king stalemated on a8: no legal moves but no check.
        var board = new Board("k7/8/1Q6/8/8/8/8/4K3 b - - 0 1");
        Assert.Equal(GameResult.Stalemate, GameState.GetResult(board));
        Assert.False(board.IsInCheck());
    }

    [Fact]
    public void HalfmoveClock100_IsDraw()
    {
        var board = new Board("4k3/8/8/8/8/8/8/4K2R w - - 100 80");
        Assert.Equal(GameResult.FiftyMoveRule, GameState.GetResult(board));
    }

    [Fact]
    public void Checkmate_TakesPrecedenceAtHalfmove100()
    {
        var board = new Board("r1bqkb1r/pppp1Qpp/2n2n2/4p3/2B1P3/8/PPPP1PPP/RNB1K1NR b KQkq - 100 4");
        Assert.Equal(GameResult.Checkmate, GameState.GetResult(board));
    }

    [Theory]
    [InlineData("7k/8/8/8/8/8/8/K7 w - - 0 1")]             // K-K.
    [InlineData("7k/8/8/8/8/8/8/KN6 w - - 0 1")]            // KN-K.
    [InlineData("7k/8/8/8/8/8/8/KB6 w - - 0 1")]            // KB-K.
    [InlineData("5b1k/8/8/8/8/8/8/K1B5 w - - 0 1")]         // Same-colour bishops.
    public void DeadMaterial_IsImmediateDraw(string fen)
    {
        var board = new Board(fen);
        Assert.True(GameState.IsDeadPosition(board));
        Assert.Equal(GameResult.InsufficientMaterial, GameState.GetResult(board));
    }

    [Theory]
    [InlineData("7k/5b2/8/8/8/8/8/K1B5 w - - 0 1")]         // Opposite-colour bishops.
    [InlineData("7k/8/8/8/8/8/8/KNN5 w - - 0 1")]           // KNN-K can reach mate.
    public void MaterialThatCanStillMate_IsNotDead(string fen)
    {
        var board = new Board(fen);
        Assert.False(GameState.IsDeadPosition(board));
        Assert.Equal(GameResult.Ongoing, GameState.GetResult(board));
    }

    [Fact]
    public void StartPosition_IsOngoing()
    {
        Assert.Equal(GameResult.Ongoing, GameState.GetResult(new Board()));
    }
}
