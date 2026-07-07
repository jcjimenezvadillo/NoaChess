namespace NoaChess.Engine.Search;

// Limits for one search: a maximum depth, a time budget, or both. The search
// runs iterative deepening until it hits whichever limit triggers first.
public readonly record struct SearchLimits(int MaxDepth, long MaxTimeMs)
{
    // Depth cap used when the search is only limited by time.
    public const int DepthUnlimited = 64;

    // Fixed-depth search with no time limit ("go depth N").
    public static SearchLimits Depth(int depth) => new(depth, long.MaxValue);

    // Time-limited search ("go movetime N" or a budget derived from the clock).
    public static SearchLimits Time(long milliseconds, int maxDepth = DepthUnlimited) =>
        new(maxDepth, milliseconds);
}
