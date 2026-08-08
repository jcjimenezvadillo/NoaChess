using NoaChess.Engine.Search;
using NoaChess.Engine.TimeManagement;
using Xunit;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// TEMPORARY measurement harness: dumps what TimeManager actually budgets for
// the clocks the lichess bot really plays, and which of the several caps is
// the one binding.
//
// Measured on the bot's own 25 most recent games (2026-08-07): it spends ~43%
// of a reasonable share of its clock, and going from a 60s base to a 300s base
// buys ZERO extra depth (median 18 either way) while leaving 214 of 300
// seconds unused. Ponder is ruled out - it would show as low spend WITH deep
// searches. So the budget itself is suspect, and this prints it directly
// instead of inferring it from games.
public class TimeBudgetProbe
{
    private readonly ITestOutputHelper _out;

    public TimeBudgetProbe(ITestOutputHelper output) => _out = output;

    [Fact]
    public void DumpBudgets()
    {
        // The bot passes MoveOverhead 100; the engine default is 30. Both are
        // shown because the reserve is multiplied by 52, so the difference is
        // 5.2s vs 1.56s taken off the top of every clock.
        foreach (int overhead in new[] { 30, 100 })
        {
            _out.WriteLine($"===== MoveOverhead = {overhead} ms =====");
            _out.WriteLine($"{"clock",8} {"inc",6} {"ply",5} {"optimum",9} {"maximum",9} "
                         + $"{"opt/clock",10}  binding cap");
            foreach ((int baseMs, int incMs) in new[]
                     {
                         (60_000, 1_000), (60_000, 2_000), (180_000, 1_000),
                         (180_000, 2_000), (300_000, 2_000), (600_000, 5_000),
                     })
            {
                foreach (int ply in new[] { 4, 20, 40 })
                {
                    // Simulate a clock that has already run down proportionally
                    // to the ply, which is the state the bot is actually in.
                    long clock = baseMs - (long)(ply / 2.0 * 1_500);
                    if (clock < 5_000)
                        clock = 5_000;

                    SearchLimits limits = TimeManager.FromClock(clock, incMs, overhead, null, ply);

                    // Re-derive the two candidate caps to name the one that bound.
                    long sustainable = incMs + clock / 16;
                    int mtg = 50;
                    long ovh = Math.Min(overhead * (2L + mtg), clock / 2);
                    long timeLeft = Math.Max(1, clock + incMs * (mtg - 1) - ovh);
                    double optExtra = Math.Clamp(1.0 + 12.0 * incMs / clock, 1.0, 1.12);
                    double optScale = Math.Min(0.0120 + Math.Pow(ply + 3.0, 0.45) * 0.0039,
                                               0.2 * clock / (double)timeLeft) * optExtra;
                    optScale *= Math.Min(1.0, 0.55 + ply * 0.025);
                    long formula = Math.Max(1, (long)(optScale * timeLeft));

                    string cap = limits.SoftTimeMs >= formula ? "formula"
                               : limits.SoftTimeMs == sustainable ? "SUSTAINABLE inc+clock/16"
                               : "maximum";
                    if (formula > sustainable && limits.SoftTimeMs <= sustainable)
                        cap = "SUSTAINABLE inc+clock/16";

                    _out.WriteLine(
                        $"{clock / 1000.0,7:F1}s {incMs / 1000.0,5:F1}s {ply,5} "
                      + $"{limits.SoftTimeMs,8}ms {limits.HardTimeMs,8}ms "
                      + $"{100.0 * limits.SoftTimeMs / clock,9:F1}%  {cap}"
                      + $"   (formula={formula}, sustainable={sustainable})");
                }
            }
            _out.WriteLine("");
        }
    }
}
