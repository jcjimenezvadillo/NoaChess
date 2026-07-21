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
