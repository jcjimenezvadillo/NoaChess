namespace NoaChess.Engine.Search;

// Limits for one search. The search runs iterative deepening until whichever
// limit triggers first:
// - MaxDepth: hard depth cap.
// - HardTimeMs: absolute deadline — the search aborts mid-iteration.
// - SoftTimeMs: target budget — no NEW iteration starts past it, but the one
//   in progress may finish. Distinguishing soft/hard lets time-managed games
//   use their budget well without ever flagging.
// - MaxNodes: node cap ("go nodes N").
// - ElapsedOffsetMs: time already charged against this move's budget before
//   the search starts. Set on a ponderhit relaunch: the reference scheduler
//   anchors its clock at "go ponder", so the pondering time counts toward the
//   budget and a long successful ponder answers almost instantly instead of
//   spending the whole optimum again over the warm TT.
public readonly record struct SearchLimits(int MaxDepth, long HardTimeMs, long SoftTimeMs, long MaxNodes,
                                           long ElapsedOffsetMs = 0)
{
    // Sentinel used when depth is not itself a limit. The old value (64)
    // made "go infinite", pondering, clock and node searches stop on their
    // own at depth 64 instead of running until their actual limit or "stop".
    public const int DepthUnlimited = int.MaxValue;

    // Search controlled exclusively by the caller's cancellation token.
    public static SearchLimits Unlimited() =>
        new(DepthUnlimited, long.MaxValue, long.MaxValue, long.MaxValue);

    // Fixed-depth search with no other limit ("go depth N").
    public static SearchLimits Depth(int depth) =>
        new(depth, long.MaxValue, long.MaxValue, long.MaxValue);

    // Exact time budget ("go movetime N"): soft and hard coincide.
    public static SearchLimits Time(long milliseconds, int maxDepth = DepthUnlimited) =>
        new(maxDepth, milliseconds, milliseconds, long.MaxValue);

    // Clock-derived budget (see TimeManager): aim for 'soft', never exceed 'hard'.
    public static SearchLimits Clock(long softMs, long hardMs) =>
        new(DepthUnlimited, hardMs, softMs, long.MaxValue);

    // Node-limited search ("go nodes N").
    public static SearchLimits Nodes(long nodes) =>
        new(DepthUnlimited, long.MaxValue, long.MaxValue, nodes);
}
