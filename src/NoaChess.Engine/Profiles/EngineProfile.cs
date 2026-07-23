namespace NoaChess.Engine.Profiles;

// A named bundle of search/time parameters. Different time controls want
// different trade-offs: at bullet speeds, re-searching and deep verification
// are luxuries — pruning harder and moving faster wins more games than
// searching "properly". Profiles keep those trade-offs in one tunable place
// (full per-time-control profiles arrive in v3.2 per the roadmap).
public sealed record EngineProfile(
    string Name,

    // Half-width of the aspiration window (centipawns). Wider = fewer
    // expensive re-searches, at the cost of slightly weaker windows.
    int AspirationWindow,

    // LMR triggers: reduce quiet moves ranked at or after this position...
    int LmrMinMoves,
    // ...when at least this depth remains.
    int LmrMinDepth)
{
    // Balanced defaults for rapid/classical play.
    public static readonly EngineProfile Default = new("Default",
        AspirationWindow: 50, LmrMinMoves: 4, LmrMinDepth: 3);

    // Bullet: prune later moves sooner and harder, and avoid re-searches
    // (the time manager itself already scales with the clock).
    public static readonly EngineProfile Bullet = new("Bullet",
        AspirationWindow: 80, LmrMinMoves: 3, LmrMinDepth: 2);

    public static EngineProfile ByName(string name) =>
        name.Equals("Bullet", StringComparison.OrdinalIgnoreCase) ? Bullet : Default;
}
