using System.Diagnostics;
using NoaChess.Core;
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
    public void OpeningDamp_FirstMoveStaysASmallShareOfTheClockDespiteIncrement()
    {
        // 3+2: the horizon folds 49 future increments into the usable time,
        // which used to budget ~7.5s optimum for the first move. The opening
        // damp must keep the first-move soft budget under ~3% of the clock.
        SearchLimits first = TimeManager.FromClock(180_000, 2_000, 30, movesToGo: null, gamePly: 0);

        Assert.True(first.SoftTimeMs < 5_400, $"soft={first.SoftTimeMs}");

        // And the damp fades out: by ply 18 the budget is back on the
        // reference curve, above the damped opening value.
        SearchLimits later = TimeManager.FromClock(180_000, 2_000, 30, movesToGo: null, gamePly: 18);
        Assert.True(later.SoftTimeMs > first.SoftTimeMs,
            $"first={first.SoftTimeMs} later={later.SoftTimeMs}");
    }

    [Fact]
    public void SustainabilityGuard_TimeTroubleSpendStaysNearTheIncrement()
    {
        // 2+1 bullet with 5s left (the Arena death spiral): the raw reference
        // formula allowed a ~4s hard deadline — 80% of the clock on one move.
        // The guard bounds the target by inc + clock/16 and the deadline by
        // inc + clock/4, so the clock stabilizes around the increment.
        SearchLimits trouble = TimeManager.FromClock(remainingMs: 5_000, incrementMs: 1_000,
                                                     moveOverheadMs: 30, movesToGo: null, gamePly: 100);

        Assert.True(trouble.SoftTimeMs <= 1_000 + 5_000 / 16, $"soft={trouble.SoftTimeMs}");
        Assert.True(trouble.HardTimeMs <= 1_000 + 5_000 / 4 - 30, $"hard={trouble.HardTimeMs}");
    }

    [Fact]
    public void SustainabilityGuard_DoesNotStrangleAHealthyClock()
    {
        // 2+1 with 90s left in the middlegame: the guard thresholds
        // (inc + clock/16 = 6.6s target) sit above the reference budget
        // (~4.3s), so normal pacing is untouched.
        SearchLimits healthy = TimeManager.FromClock(remainingMs: 90_000, incrementMs: 1_000,
                                                     moveOverheadMs: 30, movesToGo: null, gamePly: 20);

        Assert.True(healthy.SoftTimeMs > 4_000, $"soft={healthy.SoftTimeMs}");
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

    [Fact]
    public void ElapsedOffset_DefaultsToZero()
    {
        // Every existing construction path must keep a zero offset — only the
        // ponderhit relaunch sets it.
        Assert.Equal(0, SearchLimits.Clock(100, 200).ElapsedOffsetMs);
        Assert.Equal(0, TimeManager.FromClock(60_000, 600, 30).ElapsedOffsetMs);
        Assert.Equal(0, SearchLimits.Depth(10).ElapsedOffsetMs);
        Assert.Equal(0, SearchLimits.Time(1_000).ElapsedOffsetMs);
    }

    [Fact]
    public void PonderCredit_ConsumedBudget_AnswersAlmostInstantly()
    {
        // A ponderhit after a long ponder: the budget is nearly exhausted
        // before the search starts, so it must come back in well under the
        // nominal soft time — and still with a legal move (the position is
        // mid-game, 20+ legal moves, so the forced-move shortcut is not what
        // answers here).
        var engine = new ChessEngine();
        var board = new Board();
        SearchLimits limits = SearchLimits.Clock(softMs: 3_000, hardMs: 6_000)
            with { ElapsedOffsetMs = 5_900 };

        var sw = Stopwatch.StartNew();
        var result = engine.FindBestMove(board, limits);
        sw.Stop();

        Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
        Assert.True(sw.ElapsedMilliseconds < 1_500, $"took {sw.ElapsedMilliseconds}ms");
    }
}
