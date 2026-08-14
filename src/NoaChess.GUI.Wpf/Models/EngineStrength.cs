namespace NoaChess.GUI.Wpf.Models;

// How hard the engine is allowed to try.
//
// The limit is a NODE CAP, not an Elo dial. A node cap is honest: it says
// exactly what it does - the engine may look at this many positions and no more
// - and it degrades the way a weaker player does, by seeing less far rather
// than by playing deliberate nonsense. Engines that "play at 1500" by adding
// random blunders produce moves no human of any strength would choose.
//
// The names are descriptions, NOT rating claims. No gauntlet has been run at
// these caps, so putting an Elo number on them would be inventing a
// measurement, and this project does not do that.
public sealed record EngineStrength(string Name, string Detail, long MaxNodes)
{
    // Full strength: no node cap at all, only the time control.
    public static EngineStrength Full { get; } =
        new("Full strength", "everything the clock allows", 0);

    public static IReadOnlyList<EngineStrength> All { get; } =
    [
        new("Beginner", "sees one move ahead, mostly", 800),
        new("Casual", "spots simple tactics", 8_000),
        new("Club", "punishes real mistakes", 60_000),
        new("Strong", "hard work for most players", 400_000),
        Full,
    ];

    public bool IsCapped => MaxNodes > 0;

    public static EngineStrength ByName(string? name) =>
        All.FirstOrDefault(s => s.Name == name) ?? Full;
}
