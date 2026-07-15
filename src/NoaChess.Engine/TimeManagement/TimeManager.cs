using NoaChess.Engine.Search;

namespace NoaChess.Engine.TimeManagement;

// Turns a chess clock state ("go wtime ... btime ... winc ... binc ...") into
// a search budget. v2.6.5 is a direct port of a top classical engine's time
// manager (timeman.cpp): it computes two bounds,
//
// - optimumTime: the target share of the clock for this move. The search
//   modulates it every iteration with dynamic factors (falling eval, best-move
//   stability, best-move instability) and stops when the elapsed time exceeds
//   the modulated target (see AlphaBetaSearch).
// - maximumTime: the absolute deadline — the search aborts mid-iteration.
//
// Two time-control shapes are supported:
// 1) x basetime (+ z increment)          — "sudden death", bullet/blitz/rapid
// 2) x moves in y seconds (+ z increment) — classical, GUI sends "movestogo"
//
// The whole increment is folded into the usable time over the assumed move
// horizon (inc * (mtg - 1)), instead of the flat per-move percentage of
// earlier versions; MoveOverhead per expected move absorbs GUI latency.
public static class TimeManager
{
    public static SearchLimits FromClock(long remainingMs, long incrementMs, int moveOverheadMs,
                                         int? movesToGo = null, int gamePly = 0)
    {
        long time = Math.Max(1, remainingMs);

        // Maximum move horizon of 50 moves.
        int mtg = movesToGo is int m && m > 0 ? Math.Min(m, 50) : 50;

        // Time usable over the whole horizon: the clock plus the increments
        // that will keep coming back, minus overhead for each expected move.
        // Kept > 0 since it is used as a divisor.
        long timeLeft = Math.Max(1, time + incrementMs * (mtg - 1) - moveOverheadMs * (2L + mtg));

        // optScale is the fraction of timeLeft to target for this move;
        // maxScale is the multiplier from optimum to the hard deadline.
        double optScale, maxScale;

        if (movesToGo is null or <= 0)
        {
            // x basetime (+ z increment): the target share grows slowly with
            // the game ply (openings are cheap, middlegames deserve time) and
            // is capped at 20% of the actual remaining clock — with a healthy
            // increment timeLeft can exceed the clock itself. Larger
            // increments allow using a little extra.
            double optExtra = Math.Clamp(1.0 + 12.0 * incrementMs / time, 1.0, 1.12);
            optScale = Math.Min(0.0120 + Math.Pow(gamePly + 3.0, 0.45) * 0.0039,
                                0.2 * time / (double)timeLeft)
                       * optExtra;
            maxScale = Math.Min(7.0, 4.0 + gamePly / 12.0);

            // Opening damp (NoaChess addition over the reference formula).
            // timeLeft folds the whole future increment into the usable time
            // (inc x 49 on the full horizon), which inflates the early-game
            // optimum: at 3+2 the raw formula budgets ~7.5s per opening move
            // (~19s once the dynamic factors extend it), starving the
            // middlegame. Without an opening book the first moves are the
            // cheapest of the game — shrink their share and let the ply curve
            // take over from ~move 10.
            optScale *= Math.Min(1.0, 0.55 + gamePly * 0.025);
        }
        else
        {
            // x moves in y seconds (+ z increment): the clock must cover
            // exactly mtg moves until the next time control.
            optScale = Math.Min((0.88 + gamePly / 116.4) / mtg,
                                0.88 * time / (double)timeLeft);
            maxScale = Math.Min(6.3, 1.5 + 0.11 * mtg);
        }

        long optimum = Math.Max(1, (long)(optScale * timeLeft));

        // Never use more than 80% of the available clock for one move. The
        // extra -10 ms absorbs the node-batched stop-check granularity.
        long maximum = (long)Math.Min(0.8 * time - moveOverheadMs, maxScale * optimum) - 10;
        maximum = Math.Max(1, maximum);

        // Sustainability guard (v2.6.8.1, NoaChess addition, sudden death
        // only). The reference formula folds 49 future increments into the
        // usable time, betting that the increment always comes back; its only
        // brake is the 20%-of-clock cap, which lets the clock decay
        // geometrically instead of stabilizing the spend around the
        // increment. Observed in Arena bullet (2+1): 3-4s per move in the
        // middlegame bleeding 2-3s net each move, then 1-2s moves (hard
        // deadline ~4s!) with 5s on the clock — time losses in won positions.
        // Bound the target by inc + clock/16 and the hard deadline by
        // inc + clock/4: every move stays affordable, and near-exhausted
        // clocks stabilize around the increment instead of flagging.
        if (movesToGo is null or <= 0)
        {
            long sustainableOptimum = incrementMs + time / 16;
            long sustainableMaximum = Math.Max(1, incrementMs + time / 4 - moveOverheadMs);
            optimum = Math.Min(optimum, sustainableOptimum);
            maximum = Math.Min(maximum, sustainableMaximum);
        }

        if (optimum > maximum)
            optimum = maximum;

        return SearchLimits.Clock(optimum, maximum);
    }
}
