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
    public void StartPosition_IsOngoing()
    {
        Assert.Equal(GameResult.Ongoing, GameState.GetResult(new Board()));
    }
}
