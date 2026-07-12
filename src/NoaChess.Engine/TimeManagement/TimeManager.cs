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
// v2.6.4 rewrites the scheduler in the mould of a top classical engine:
// - Spend ~85% of the increment (an engine that folds the whole increment into
//   the usable time and scales gets the same effect; the flat 85% here is the
//   simpler, equally-effective form).
// - Adaptive horizon: the assumed remaining-move count shrinks as the game
//   advances, so a growing share of the clock is spent in the middlegame where
//   the decisions matter (a ply-scaled optimum curve
//   0.0120 + pow(ply+3, 0.45) * 0.0039).
// The dynamic factors (best-move instability, falling-eval extension) are
// applied per iteration in AlphaBetaSearch, which owns the search state.
public static class TimeManager
{
    // Fraction of the increment spent each move (equivalent to folding the
    // increment into the usable time). The remaining 15% is a safety reserve.
    private const long IncrementUsePercent = 85;

    public static SearchLimits FromClock(long remainingMs, long incrementMs, int moveOverheadMs,
                                         int? movesToGo = null, int? assumedMovesToGo = null,
                                         int gamePly = 0)
    {
        // Time actually usable this move, keeping the overhead margin intact.
        long usable = Math.Max(1, remainingMs - moveOverheadMs);

        // Adaptive horizon (ply curve). Early in the game the clock is assumed
        // to cover many moves (spend a small slice); as ply grows the assumed
        // horizon shrinks, spending a slightly larger slice per move. The
        // profile/movestogo overrides still win. The divisor is deliberately
        // conservative (~48 in the opening down to ~38 in the middlegame): the
        // per-move budget must be a small fraction of the clock so the game
        // lasts. An earlier tuning used ~31 here and, combined with the
        // best-move-instability multiplier, spent 3-4x too much in the volatile
        // opening and burned the whole clock in the first moves.
        int horizon = assumedMovesToGo
            ?? (int)Math.Clamp(52.0 - Math.Pow(gamePly + 3, 0.45) * 2.2, 38.0, 52.0);

        // Divisor: with "N moves to the next time control" (classical TCs,
        // sent by the GUI as "movestogo N") the clock must cover exactly
        // those moves — plus a small safety margin so the last one is not
        // played on fumes. Otherwise assume the adaptive horizon.
        int divisor = movesToGo is int m && m > 0
            ? Math.Min(m + 2, horizon)
            : horizon;

        // Target: a clock slice plus most of the increment. NOTE: the bounds
        // are applied with Min/Max, not Math.Clamp — with a nearly exhausted
        // clock the "minimum" can exceed the cap and Clamp would THROW
        // (an engine crash at zero clock is a guaranteed time forfeit).
        long softCap = usable / 3 + 1;
        long soft = usable / divisor + incrementMs * IncrementUsePercent / 100;
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
