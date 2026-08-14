using System.IO;
using System.Text.Json;

namespace NoaChess.GUI.Wpf.Services;

// User preferences that survive a restart. Deliberately tiny and forgiving:
// a corrupt or missing file just means defaults, never a crash on startup.
public sealed class AppSettings
{
    public string BoardTheme { get; set; } = "Noa";

    // The time control, stored field by field rather than as an object so an
    // older settings file still loads with sensible values for the rest.
    public string TimeControlKind { get; set; } = "MoveTime";
    public int MoveTimeMs { get; set; } = 3000;
    public int ClockBaseMs { get; set; } = 300_000;
    public int ClockIncrementMs { get; set; } = 3000;
    public int FixedDepth { get; set; } = 10;

    // Named in the settings file rather than stored as a number, so a level
    // renamed or re-tuned later still loads as itself.
    public string EngineStrength { get; set; } = "Full strength";

    // Who plays each colour, and the external engines that have been added.
    public string WhitePlayer { get; set; } = "human";
    public string BlackPlayer { get; set; } = "builtin";
    public List<string> ExternalEngines { get; set; } = [];

    // Whether the engine varies its opening. Without it every game against it
    // is the same game.
    public bool VaryOpening { get; set; } = true;

    public int Threads { get; set; } = 1;
    public int HashMb { get; set; } = 128;
    public int AnalysisMaxDepth { get; set; } = 32;

    // Depth every position gets during a whole-game review. Fixed rather than
    // timed on purpose: a time budget would make the same move a blunder in one
    // run and fine in the next, depending on how busy the machine was.
    public int ReviewDepth { get; set; } = 12;

    // Whether a review also looks for the positions where the choice mattered.
    // It is the slower half of the review, so it can be turned off.
    public bool FindDecisionPoints { get; set; } = true;

    // Folder holding the Syzygy endgame tables, remembered between runs so it
    // is chosen once rather than every session.
    public string SyzygyPath { get; set; } = "";
    public bool ShowCoordinates { get; set; } = true;
    public bool ShowLegalMoves { get; set; } = true;
    public bool AnalyseWhileIdle { get; set; } = true;

    // Where the window was last time. Restored on the next run, which is what
    // separates a program you use from one you set up again every morning.
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool WindowMaximised { get; set; }

    // Milliseconds between moves when replaying a game.
    public int ReplaySpeedMs { get; set; } = 900;

    // Version whose notes the user has already seen. The changelog opens by
    // itself exactly once per new build instead of on every launch: a modal in
    // front of the board every single time is a toll, not a feature.
    public string LastSeenVersion { get; set; } = "";

    public Models.TimeControl ReadTimeControl() => TimeControlKind switch
    {
        "Clock" => Models.TimeControl.Game(ClockBaseMs, ClockIncrementMs),
        "Depth" => Models.TimeControl.FixedDepth(FixedDepth),
        _ => Models.TimeControl.PerMove(MoveTimeMs),
    };

    public void WriteTimeControl(Models.TimeControl control)
    {
        TimeControlKind = control.Kind.ToString();
        switch (control.Kind)
        {
            case Models.TimeControlKind.Clock:
                ClockBaseMs = control.BaseMs;
                ClockIncrementMs = control.IncrementMs;
                break;
            case Models.TimeControlKind.Depth:
                FixedDepth = control.Depth;
                break;
            default:
                MoveTimeMs = control.MoveTimeMs;
                break;
        }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NoaChess", "gui-settings.json");

    public static AppSettings Load()
    {
        try
        {
            string path = FilePath;
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch
        {
            // A settings file we cannot read is not worth a dialog: the user
            // gets defaults and the file is rewritten on the next save.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string path = FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Saving preferences is best-effort; failing to do so must never
            // interrupt the user's game.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
}
