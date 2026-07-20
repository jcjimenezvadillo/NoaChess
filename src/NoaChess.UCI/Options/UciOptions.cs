namespace NoaChess.UCI.Options;

// The engine options exposed over UCI ("setoption name X value Y").
// - Hash: transposition table size in MB.
// - Threads: accepted for GUI compatibility, but only 1 is supported until
//   Lazy SMP arrives in v3.0.
// - MoveOverhead: per-move milliseconds reserved for GUI/network latency.
//   The time manager deducts it once per expected remaining move
//   (overhead x horizon), so the default must stay small: 100 ms would
//   reserve over 5 s of the clock and collapse low-clock bullet endgames to
//   instant moves. Raise it for laggy online play, not for local GUIs.
// - UseNNUE / EvalFile: neural evaluation switch and model path (v2.0).
public sealed class UciOptions
{
    public int Hash { get; private set; } = 64;
    public int Threads { get; private set; } = 1;
    public int MoveOverhead { get; private set; } = 30;
    public bool Ponder { get; private set; }
    public bool UseNnue { get; private set; }
    public string EvalFile { get; private set; } = "";
    public string Profile { get; private set; } = "Default";
    public string DebugLogFile { get; private set; } = "";

    // ---- Syzygy endgame tablebases ----
    // SyzygyPath: semicolon-separated directories holding the .rtbw/.rtbz
    // files; empty disables probing entirely.
    // SyzygyProbeDepth: probing costs a file read, so shallow nodes can skip
    // it. 1 means "probe everywhere the piece count allows".
    // SyzygyProbeLimit: never probe positions with more men than this, even if
    // larger tables happen to be installed.
    // Syzygy50MoveRule: when false, cursed wins and blessed losses are treated
    // as plain wins and losses (used for analysis where the rule is ignored).
    public string SyzygyPath { get; private set; } = "";
    public int SyzygyProbeDepth { get; private set; } = 1;
    public int SyzygyProbeLimit { get; private set; } = 7;
    public bool Syzygy50MoveRule { get; private set; } = true;

    // Prints the option declarations the GUI expects right after "id".
    public void Print(TextWriter output)
    {
        output.WriteLine("option name Hash type spin default 64 min 1 max 1024");
        output.WriteLine("option name Threads type spin default 1 min 1 max 1");
        output.WriteLine("option name MoveOverhead type spin default 30 min 0 max 5000");
        output.WriteLine("option name Ponder type check default false");
        output.WriteLine("option name UseNNUE type check default false");
        output.WriteLine("option name EvalFile type string default <empty>");
        output.WriteLine("option name Profile type combo default Default var Default var Bullet");
        output.WriteLine("option name SyzygyPath type string default <empty>");
        output.WriteLine("option name SyzygyProbeDepth type spin default 1 min 1 max 100");
        output.WriteLine("option name SyzygyProbeLimit type spin default 7 min 0 max 7");
        output.WriteLine("option name Syzygy50MoveRule type check default true");
        output.WriteLine("option name Debug Log File type string default <empty>");
    }

    // Applies "setoption name <name> value <value>". Returns the canonical
    // option name that changed, or null if the option is unknown/invalid
    // (UCI mandates silently ignoring those).
    public string? Set(string name, string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "hash" when int.TryParse(value, out int hash):
                Hash = Math.Clamp(hash, 1, 1024);
                return "Hash";

            case "threads" when int.TryParse(value, out int threads):
                Threads = Math.Clamp(threads, 1, 1); // Single-threaded until v3.0.
                return "Threads";

            case "moveoverhead" when int.TryParse(value, out int overhead):
                MoveOverhead = Math.Clamp(overhead, 0, 5000);
                return "MoveOverhead";

            case "ponder" when bool.TryParse(value, out bool ponder):
                Ponder = ponder; // The GUI drives pondering; we just declare support.
                return "Ponder";

            case "usennue" when bool.TryParse(value, out bool useNnue):
                UseNnue = useNnue;
                return "UseNNUE";

            case "evalfile":
                EvalFile = value == "<empty>" ? "" : value;
                return "EvalFile";

            case "profile":
                Profile = value.Equals("Bullet", StringComparison.OrdinalIgnoreCase) ? "Bullet" : "Default";
                return "Profile";

            case "syzygypath":
                SyzygyPath = value == "<empty>" ? "" : value;
                return "SyzygyPath";

            case "syzygyprobedepth" when int.TryParse(value, out int probeDepth):
                SyzygyProbeDepth = Math.Clamp(probeDepth, 1, 100);
                return "SyzygyProbeDepth";

            case "syzygyprobelimit" when int.TryParse(value, out int probeLimit):
                SyzygyProbeLimit = Math.Clamp(probeLimit, 0, 7);
                return "SyzygyProbeLimit";

            case "syzygy50moverule" when bool.TryParse(value, out bool rule50):
                Syzygy50MoveRule = rule50;
                return "Syzygy50MoveRule";

            case "debug log file":
                DebugLogFile = value == "<empty>" ? "" : value;
                return "Debug Log File";

            default:
                return null;
        }
    }
}
