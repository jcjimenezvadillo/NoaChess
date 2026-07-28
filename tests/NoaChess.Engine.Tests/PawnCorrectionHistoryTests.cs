using NoaChess.Core;
using NoaChess.Engine.Heuristics;

namespace NoaChess.Engine.Tests;

public sealed class PawnCorrectionHistoryTests
{
    [Fact]
    public void UpdateCorrectsTowardObservedResidualAndClearResets()
    {
        var board = new Board();
        var history = new PawnCorrectionHistory();

        Assert.Equal(25, history.Correct(board, 25));

        history.Update(board, errorCp: 120, depth: 8);
        int corrected = history.Correct(board, 25);
        Assert.InRange(corrected, 26, 145);

        history.Clear();
        Assert.Equal(25, history.Correct(board, 25));
    }

    [Fact]
    public void EntriesAreSeparatedBySideToMove()
    {
        var board = new Board();
        var history = new PawnCorrectionHistory();
        history.Update(board, errorCp: 120, depth: 8);

        board.MakeNullMove();
        Assert.Equal(25, history.Correct(board, 25));
        board.UnmakeNullMove();
        Assert.True(history.Correct(board, 25) > 25);
    }
}
