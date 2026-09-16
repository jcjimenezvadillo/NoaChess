using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.UCI;
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

    // ---- Rating scaling (see UciLoop.ScaleContempt for the measurement that
    // says a FLAT contempt is the wrong shape) ----

    [Fact]
    public void WithoutOwnRating_ContemptStaysFlat()
    {
        // Nobody told us our own rating, so there is no gap to scale by and
        // the value is used as given. This is the pre-scaling behaviour.
        Assert.Equal(25, UciLoop.ScaleContempt(25, ownRating: 0, opponentRating: 3300));
        Assert.Equal(25, UciLoop.ScaleContempt(25, ownRating: 3400, opponentRating: null));
    }

    [Theory]
    [InlineData(3400, 3300, 25)]  // +100 or more: the whole thing
    [InlineData(3400, 3200, 25)]  // capped, not extrapolated
    [InlineData(3400, 3350, 12)]  // +50: half of it
    [InlineData(3400, 3400, 0)]   // level: none
    [InlineData(3300, 3400, 0)]   // outrated: none, never negative
    public void ContemptFollowsTheRatingGap(int own, int opp, int expected)
        => Assert.Equal(expected, UciLoop.ScaleContempt(25, own, opp));

    [Fact]
    public void OpponentRatingIsParsedOutOfTheUciOpponentString()
    {
        // "UCI_Opponent value <title> <elo> <computer|human> <name>", the shape
        // lichess-bot sends. A rating that is absent or "none" must leave the
        // value unset rather than guessed.
        var options = new NoaChess.UCI.Options.UciOptions();

        options.Set("UCI_Opponent", "none 2803 computer PlumBoi");
        Assert.Equal(2803, options.OpponentRating);

        options.Set("UCI_Opponent", "GM none human Magnus Carlsen");
        Assert.Null(options.OpponentRating);
    }
}
