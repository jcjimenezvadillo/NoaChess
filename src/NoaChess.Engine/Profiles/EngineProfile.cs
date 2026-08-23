namespace NoaChess.Engine.Profiles;

// A named bundle of search/time parameters, on the theory that different time
// controls want different trade-offs.
//
// THAT THEORY IS NOW MEASURED AND IT DID NOT SURVIVE: see the Bullet profile
// below, which loses 15.6 Elo at the time control it was designed for. The
// header used to assert the opposite as if it were established, which is how a
// placeholder written from intuition ends up looking like a finding.
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

    // ---- MEASURED AND FALSIFIED (2026-08-22): DO NOT SWITCH THE BOTS TO THIS ----
    //
    // The idea was that at bullet speeds, pruning later moves sooner and
    // avoiding re-searches wins more games than searching properly. It had
    // never been measured. It is wrong:
    //
    //     691 games at 10+0.1   -15.6 Elo [-31.8, +0.5]   LLR -7.61   H0
    //
    // One binary played both sides, selected by this option, so the two arms
    // could not differ in anything else. The instrument was checked first: at
    // depth 13 this profile searches 367,064 nodes against Default's 425,317
    // and returns a different PV, so a silently ignored option would have shown
    // up as a perfect draw rather than as a result.
    //
    // The obvious objection was checked and does not hold. Time forfeits split
    // 2 for Bullet against 4 for Default, so CPU contention was not penalising
    // the faster profile - if anything it favoured it. The seven unterminated
    // games are the last seven in the file, i.e. the ones in flight when the
    // SPRT hit its bound, not crashes.
    //
    // BOTH BOTS PLAY BULLET AND NEITHER SETS Profile, so they run Default and
    // that is CORRECT. Nothing to change there; this note exists so that nobody
    // "fixes" it. The option is kept rather than deleted because removing a
    // declared UCI combo is an interface change, but it is a trap, not a
    // feature: the numbers below are worse than the defaults at the very time
    // control they were invented for.
    public static readonly EngineProfile Bullet = new("Bullet",
        AspirationWindow: 80, LmrMinMoves: 3, LmrMinDepth: 2);

    // ---- EXPERIMENT (2026-08-22): which of Bullet's three knobs cost the 15.6? ----
    //
    // Bullet moved THREE things at once - the aspiration window, the LMR move
    // threshold and the LMR depth threshold - so its -15.6 cannot be attributed
    // to any of them. That is the same mistake that killed six candidates in one
    // week here by measuring them together.
    //
    // These two profiles bisect it. Each changes exactly ONE axis away from
    // Default, so between them and Bullet the three knobs are separated:
    //
    //     WideWindow   the aspiration window alone, 50 -> 80
    //     EarlyLmr     the LMR triggers alone, 4/3 -> 3/2
    //
    // The prior worth stating: none of Default's three constants has any
    // measured provenance either. The header called them "balanced defaults"
    // in the same voice that called Bullet a good idea, and that voice has now
    // been wrong once by 15.6 Elo. If the window turns out to be the whole cost,
    // then earlier LMR is exonerated and may even be a gain, which is a change
    // worth having.
    public static readonly EngineProfile WideWindow = new("WideWindow",
        AspirationWindow: 80, LmrMinMoves: 4, LmrMinDepth: 3);

    public static readonly EngineProfile EarlyLmr = new("EarlyLmr",
        AspirationWindow: 50, LmrMinMoves: 3, LmrMinDepth: 2);

    public static EngineProfile ByName(string name) =>
        name.Equals("WideWindow", StringComparison.OrdinalIgnoreCase) ? WideWindow :
        name.Equals("EarlyLmr", StringComparison.OrdinalIgnoreCase) ? EarlyLmr :
        name.Equals("Bullet", StringComparison.OrdinalIgnoreCase) ? Bullet : Default;
}
