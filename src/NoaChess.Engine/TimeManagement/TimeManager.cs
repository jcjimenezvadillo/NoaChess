using NoaChess.Engine.Search;

namespace NoaChess.Engine.TimeManagement;

// Turns a chess clock state ("go wtime ... btime ... winc ... binc ...") into
// a search budget. Goals, per the roadmap:
// - Never lose on time (hard deadline well below the remaining clock,
//   MoveOverhead absorbs GUI/network latency).
// - Do not burn the whole clock in the opening (the soft budget assumes the
//   game still has many moves to go).
// - Let the increment be spent (it comes back every move anyway).
//
// The formula is deliberately simple; smarter schemes (score-instability
// extensions, easy-move cuts) come with competition profiles in later versions.
public static class TimeManager
{
    // How many future moves the remaining clock is assumed to cover when the
    // GUI does not say ("sudden death" time controls) and no profile says
    // otherwise.
    private const int DefaultAssumedMovesToGo = 25;

    public static SearchLimits FromClock(long remainingMs, long incrementMs, int moveOverheadMs,
                                         int? movesToGo = null, int? assumedMovesToGo = null)
    {
        int horizon = assumedMovesToGo ?? DefaultAssumedMovesToGo;

        // Time actually usable this move, keeping the overhead margin intact.
        long usable = Math.Max(1, remainingMs - moveOverheadMs);

        // Divisor: with "N moves to the next time control" (classical TCs,
        // sent by the GUI as "movestogo N") the clock must cover exactly
        // those moves — plus a small safety margin so the last one is not
        // played on fumes. Otherwise assume a fixed horizon.
        int divisor = movesToGo is int m && m > 0
            ? Math.Min(m + 2, horizon)
            : horizon;

        // Target: a clock slice plus most of the increment. NOTE: the bounds
        // are applied with Min/Max, not Math.Clamp — with a nearly exhausted
        // clock the "minimum" can exceed the cap and Clamp would THROW
        // (an engine crash at zero clock is a guaranteed time forfeit).
        long softCap = usable / 3 + 1;
        long soft = usable / divisor + incrementMs / 2;
        soft = Math.Min(Math.Max(soft, 10), softCap);

        // Absolute cap: even a difficult position may not eat half the clock.
        long hard = Math.Min(soft * 4, usable / 2 + 1);

        // Final safety buffer: GUIs add fixed per-move friction (process
        // scheduling, I/O, board updates) beyond MoveOverhead. Over a
        // 70-move game those milliseconds add up; never spend the clock down
        // to the wire.
        hard = Math.Max(1, Math.Min(hard, usable - 150));
        soft = Math.Max(1, Math.Min(soft, hard));

        return SearchLimits.Clock(soft, hard);
    }
}
