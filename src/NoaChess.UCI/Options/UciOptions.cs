namespace NoaChess.UCI.Options;

// The engine options exposed over UCI ("setoption name X value Y").
// - Hash: transposition table size in MB.
// - Threads: accepted for GUI compatibility, but only 1 is supported until
//   Lazy SMP arrives in v3.0.
// - MoveOverhead: milliseconds subtracted from every time budget to absorb
//   GUI/network latency (prevents losing on time by a few ms).
// - UseNNUE / EvalFile: neural evaluation switch and model path (v2.0).
public sealed class UciOptions
{
    public int Hash { get; private set; } = 64;
    public int Threads { get; private set; } = 1;
    public int MoveOverhead { get; private set; } = 100;
    public bool Ponder { get; private set; }
    public bool UseNnue { get; private set; }
    public string EvalFile { get; private set; } = "";
    public string Profile { get; private set; } = "Default";

    // Prints the option declarations the GUI expects right after "id".
    public void Print(TextWriter output)
    {
        output.WriteLine("option name Hash type spin default 64 min 1 max 1024");
        output.WriteLine("option name Threads type spin default 1 min 1 max 1");
        output.WriteLine("option name MoveOverhead type spin default 100 min 0 max 5000");
        output.WriteLine("option name Ponder type check default false");
        output.WriteLine("option name UseNNUE type check default false");
        output.WriteLine("option name EvalFile type string default <empty>");
        output.WriteLine("option name Profile type combo default Default var Default var Bullet");
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

            default:
                return null;
        }
    }
}
