using NoaChess.Core;
using NoaChess.Engine;
using Xunit;

namespace NoaChess.Engine.Tests;

// Contempt has exactly one way to go catastrophically wrong and it is silent:
// the sign. Negamax scores are from the side to move's point of view, so a
// draw score that does not flip with the ply makes the engine SEEK the draws
// it was asked to avoid, and nothing about the search looks broken while it
// happens. These tests pin the sign to a position whose value is not a matter
// of opinion - king and bishop against king is a draw by insufficient
// material, scored through the same DrawScore path as every repetition.
public class ContemptTests
{
    private const string DeadDraw = "8/8/4k3/8/8/4KB2/8/8 w - - 0 1";

    [Fact]
    public void ZeroContempt_ScoresADeadDrawAtZero()
    {
        var engine = new ChessEngine { ContemptCp = 0 };
        var result = engine.FindBestMove(new Board(DeadDraw), depth: 8);

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void PositiveContempt_MakesADeadDrawCostUsThatMuch()
    {
        // We are the side to move at the root, so the draw is worth exactly
        // MINUS the contempt to us. A positive score here would mean the
        // engine is being paid to draw.
        var engine = new ChessEngine { ContemptCp = 40 };
        var result = engine.FindBestMove(new Board(DeadDraw), depth: 8);

        Assert.Equal(-40, result.Score);
    }

    [Fact]
    public void NegativeContempt_MirrorsIt()
    {
        // The control. A negative setting is the "I am worse, a draw suits me"
        // case, and it has to move the score the other way by the same amount.
        var engine = new ChessEngine { ContemptCp = -40 };
        var result = engine.FindBestMove(new Board(DeadDraw), depth: 8);

        Assert.Equal(40, result.Score);
    }

    [Fact]
    public void DefaultEngine_CarriesNoContempt()
    {
        // The mechanism ships inert: every measurement taken before it existed
        // has to stay reproducible with a default-constructed engine.
        Assert.Equal(0, new ChessEngine().ContemptCp);
    }
}
