namespace NoaChess.UCI.Options;

// The engine options exposed over UCI ("setoption name X value Y").
// v1.0 set, per the roadmap:
// - Hash: transposition table size in MB.
// - Threads: accepted for GUI compatibility, but only 1 is supported until
//   Lazy SMP arrives in v3.0.
// - MoveOverhead: milliseconds subtracted from every time budget to absorb
//   GUI/network latency (prevents losing on time by a few ms).
// - UseNNUE: reserved flag, always false until v2.0.
public sealed class UciOptions
{
    public int Hash { get; private set; } = 64;
    public int Threads { get; private set; } = 1;
    public int MoveOverhead { get; private set; } = 30;
    public bool UseNnue { get; private set; }

    // Prints the option declarations the GUI expects right after "id".
    public void Print(TextWriter output)
    {
        output.WriteLine("option name Hash type spin default 64 min 1 max 1024");
        output.WriteLine("option name Threads type spin default 1 min 1 max 1");
        output.WriteLine("option name MoveOverhead type spin default 30 min 0 max 5000");
        output.WriteLine("option name UseNNUE type check default false");
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

            case "usennue" when bool.TryParse(value, out bool useNnue):
                UseNnue = false && useNnue; // No NNUE until v2.0.
                return "UseNNUE";

            default:
                return null;
        }
    }
}
