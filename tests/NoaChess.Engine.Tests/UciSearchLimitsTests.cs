using NoaChess.Core;
using NoaChess.Engine.Search;
using NoaChess.Engine.TimeManagement;
using NoaChess.UCI;

namespace NoaChess.Engine.Tests;

// Regression tests for the UCI "go" parser. Real GUIs may combine clock,
// depth and node constraints; SearchLimits must preserve all of them.
public class UciSearchLimitsTests
{
    private static UciLoop NewLoop() => new(TextReader.Null, TextWriter.Null);

    [Fact]
    public void ClockDepthAndNodes_AreAppliedTogether()
    {
        SearchLimits expectedClock = TimeManager.FromClock(
            remainingMs: 60_000, incrementMs: 600, moveOverheadMs: 30,
            movesToGo: null, gamePly: 0);

        SearchLimits actual = NewLoop().ParseLimits(
            ["go", "wtime", "60000", "winc", "600", "depth", "12", "nodes", "345678"]);

        Assert.Equal(12, actual.MaxDepth);
        Assert.Equal(345_678, actual.MaxNodes);
        Assert.Equal(expectedClock.SoftTimeMs, actual.SoftTimeMs);
        Assert.Equal(expectedClock.HardTimeMs, actual.HardTimeMs);
    }

    [Fact]
    public void MoveTimeAndClock_UseTheTighterTimeBounds()
    {
        SearchLimits clock = TimeManager.FromClock(
            remainingMs: 90_000, incrementMs: 1_000, moveOverheadMs: 30,
            movesToGo: null, gamePly: 0);

        SearchLimits actual = NewLoop().ParseLimits(
            ["go", "wtime", "90000", "winc", "1000", "movetime", "500"]);

        Assert.Equal(Math.Min(clock.SoftTimeMs, 500), actual.SoftTimeMs);
        Assert.Equal(Math.Min(clock.HardTimeMs, 500), actual.HardTimeMs);
        Assert.Equal(SearchLimits.DepthUnlimited, actual.MaxDepth);
    }

    [Fact]
    public void DepthAndNodesWithoutClock_AreAppliedTogether()
    {
        SearchLimits actual = NewLoop().ParseLimits(
            ["go", "nodes", "12345", "depth", "9"]);

        Assert.Equal(9, actual.MaxDepth);
        Assert.Equal(12_345, actual.MaxNodes);
        Assert.Equal(long.MaxValue, actual.HardTimeMs);
        Assert.Equal(long.MaxValue, actual.SoftTimeMs);
    }

    // 2026-10-04: an in-place ponderhit conversion parses the clock on the UCI
    // thread while a single-threaded search is making moves on the loop's own
    // board, so the side to move and the move number must come from the root
    // captured at "go ponder", never from the board.
    [Fact]
    public void PonderRoot_BudgetsFromThatSidesClock()
    {
        // The loop's board is the start position, White to move; the root says
        // Black at move 30. Black's 10 s against 60 s: ClockDeficitBrake halves
        // the scale.
        SearchLimits actual = NewLoop().ParseLimits(
            ["go", "wtime", "60000", "btime", "10000", "winc", "0", "binc", "500"],
            root: (Color.Black, 30));
        SearchLimits expected = TimeManager.FromClock(
            remainingMs: 10_000, incrementMs: 500, moveOverheadMs: 30,
            movesToGo: null, gamePly: 59, timeScalePercent: 50);

        Assert.Equal(expected.SoftTimeMs, actual.SoftTimeMs);
        Assert.Equal(expected.HardTimeMs, actual.HardTimeMs);
    }

    // 2026-10-04: a large clock lead clamps the optimum onto the maximum, and
    // soft == hard used to read as an explicit movetime (no forced-move cut,
    // the whole budget spent). The budget must stay clock-managed; a movetime
    // that binds the deadline must still be a movetime.
    [Fact]
    public void ClampedClockBudget_StaysClockManaged_AndMoveTimeDoesNot()
    {
        string[] clock = ["go", "wtime", "8000", "btime", "1300", "winc", "1000", "binc", "1000"];
        SearchLimits lead = NewLoop().ParseLimits(clock, root: (Color.White, 31));
        Assert.Equal(lead.SoftTimeMs, lead.HardTimeMs);
        Assert.True(lead.ClockManaged);
        Assert.True(lead.IsClockMode);

        SearchLimits moveTime = NewLoop().ParseLimits([.. clock, "movetime", "500"], root: (Color.White, 31));
        Assert.Equal(500, moveTime.HardTimeMs);
        Assert.False(moveTime.IsClockMode);

        Assert.False(NewLoop().ParseLimits(["go", "movetime", "500"]).IsClockMode);
    }

    // The PonderContinue gate reads the opponent's clock as "go ponder" sent
    // it, and that clock kept running for the whole ponder (2026-10-04).
    [Fact]
    public void PonderContinueGate_CorrectsTheOpponentsClockForThePonder()
    {
        // 30 s against 25 s is 120%, under a 125% lead...
        Assert.False(UciLoop.PonderContinueFires(30_000, 25_000, ponderedMs: 0, leadPercent: 125));
        // ...but their clock ran 2 s during the ponder: 30 s against 23 s.
        Assert.True(UciLoop.PonderContinueFires(30_000, 25_000, ponderedMs: 2_000, leadPercent: 125));
        // A missing clock never fires.
        Assert.False(UciLoop.PonderContinueFires(null, 25_000, ponderedMs: 0, leadPercent: 125));
        Assert.False(UciLoop.PonderContinueFires(30_000, null, ponderedMs: 0, leadPercent: 125));
        // An opponent clock the ponder ran out (a scramble, latency inside the
        // ponder time) is the biggest lead there is: it fires while this side
        // has time, and only then.
        Assert.True(UciLoop.PonderContinueFires(30_000, 1_000, ponderedMs: 2_000, leadPercent: 125));
        Assert.True(UciLoop.PonderContinueFires(30_000, 2_000, ponderedMs: 2_000, leadPercent: 125));
        Assert.False(UciLoop.PonderContinueFires(0, 1_000, ponderedMs: 2_000, leadPercent: 125));
    }

    [Fact]
    public void Infinite_HasNoArtificialDepthCap()
    {
        SearchLimits actual = NewLoop().ParseLimits(["go", "infinite"]);

        Assert.Equal(int.MaxValue, actual.MaxDepth);
        Assert.Equal(long.MaxValue, actual.MaxNodes);
        Assert.Equal(long.MaxValue, actual.HardTimeMs);
        Assert.Equal(long.MaxValue, actual.SoftTimeMs);
    }
}
