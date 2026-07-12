using NoaChess.Engine.Search;
using NoaChess.Engine.TimeManagement;
using Xunit;

namespace NoaChess.Engine.Tests;

// v2.6.4: adaptive time management. The budget carries a soft target and a
// hard deadline; the dynamic instability/falling-eval scaling lives in the
// search loop, so these tests pin the base FromClock contract.
public class TimeManagerTests
{
    [Fact]
    public void Clock_ProducesSoftBelowHardBelowUsable()
    {
        SearchLimits limits = TimeManager.FromClock(remainingMs: 60_000, incrementMs: 600,
                                                    moveOverheadMs: 30, movesToGo: null, gamePly: 20);

        Assert.True(limits.SoftTimeMs > 0);
        Assert.True(limits.SoftTimeMs <= limits.HardTimeMs);
        Assert.True(limits.HardTimeMs < 60_000);
    }

    [Fact]
    public void Increment_IsSpentAtEightyFivePercent()
    {
        // With a huge clock the horizon slice is negligible, so the soft budget
        // is dominated by the increment share (85%).
        SearchLimits limits = TimeManager.FromClock(remainingMs: 10_000_000, incrementMs: 1_000,
                                                    moveOverheadMs: 0, movesToGo: null, gamePly: 20);

        // 85% of the increment, plus a small clock slice — but the softCap
        // (usable/3) is enormous here, so this is not clipped. Expect >= 850ms
        // (the increment share alone) with the tiny slice on top.
        Assert.True(limits.SoftTimeMs >= 850, $"soft={limits.SoftTimeMs}");
    }

    [Fact]
    public void AdaptiveHorizon_SpendsMorePerMoveInMiddlegameThanOpening()
    {
        // Same clock, no increment: a later ply assumes fewer remaining moves,
        // so a larger slice of the clock is budgeted per move.
        SearchLimits opening = TimeManager.FromClock(60_000, 0, 0, movesToGo: null, gamePly: 0);
        SearchLimits middle = TimeManager.FromClock(60_000, 0, 0, movesToGo: null, gamePly: 40);

        Assert.True(middle.SoftTimeMs > opening.SoftTimeMs,
            $"opening={opening.SoftTimeMs} middle={middle.SoftTimeMs}");
    }

    [Fact]
    public void NearlyExhaustedClock_DoesNotThrowAndStaysPositive()
    {
        // The minimum floor can exceed the caps at near-zero time; the code
        // must use Min/Max (never Math.Clamp) so it degrades instead of throwing.
        SearchLimits limits = TimeManager.FromClock(remainingMs: 40, incrementMs: 0,
                                                    moveOverheadMs: 30, movesToGo: null, gamePly: 30);

        Assert.True(limits.SoftTimeMs >= 1);
        Assert.True(limits.HardTimeMs >= 1);
        Assert.True(limits.SoftTimeMs <= limits.HardTimeMs);
    }

    [Fact]
    public void MovesToGo_TightensBudgetForTheNextControl()
    {
        // With only two moves to the next control the clock must be split over
        // those moves, so each gets far more than the sudden-death slice.
        SearchLimits suddenDeath = TimeManager.FromClock(60_000, 0, 0, movesToGo: null, gamePly: 20);
        SearchLimits twoMoves = TimeManager.FromClock(60_000, 0, 0, movesToGo: 2, gamePly: 20);

        Assert.True(twoMoves.SoftTimeMs > suddenDeath.SoftTimeMs,
            $"suddenDeath={suddenDeath.SoftTimeMs} twoMoves={twoMoves.SoftTimeMs}");
    }
}
