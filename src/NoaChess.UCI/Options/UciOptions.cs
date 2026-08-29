using System.Linq;
namespace NoaChess.UCI.Options;

// The engine options exposed over UCI ("setoption name X value Y").
// - Hash: transposition table size in MB.
// - Threads: number of parallel search threads (Lazy SMP). 1 keeps the exact
//   single-threaded search; more threads share the transposition table.
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
    public bool UseNnueExplicitlySet { get; private set; }
    public string EvalFile { get; private set; } = "";
    public string Profile { get; private set; } = "Default";
    public bool Optimism { get; private set; }
    public bool NmpEvalGate { get; private set; }
    public bool PruningLadder { get; private set; }
    public bool PruningLadderFutility { get; private set; } = true;

    // Must match EngineProfile.ByName and the combo declaration in Print().
    private static readonly string[] KnownProfiles =
        ["Default", "Bullet", "WideWindow", "EarlyLmr"];
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
    // ---- DEFAULT LOWERED 7 -> 5 (2026-08-22), and the reason is not storage ----
    //
    // THE COMPLAINT THAT STARTED IT. Two bot games where the engine gave away a
    // QUEEN for a pawn and, in another, a BISHOP for a pawn. Both moves won, but
    // no other engine plays them, and the cause is this option.
    //
    // WHY IT HAPPENS. A tablebase win is scored TbWin - ply, so entering the
    // tables SOONER scores HIGHER - and the way to enter sooner is to take
    // pieces off the board. The position is not better for having fewer pieces;
    // only the scoring says so. The ply term is borrowed from mate scoring,
    // where reaching mate sooner genuinely is better, and here it measures the
    // wrong distance entirely: distance to entering the table, not distance to
    // winning. With 6-man tables loaded, a single sacrifice from a 7-man
    // position buys a "proven win" worth ~19,987 against a heuristic +1,500, so
    // the trade always looks good.
    //
    // MEASURED, both positions, same binary, only this option changed:
    //     limit 7   Qxa5+ (queen for a pawn)      /  score 19981, keeps Bxf5
    //     limit 5   Qe3   (keeps the queen)       /  Kf2, score 1836, keeps the bishop
    // Lowering the limit removes both moves. It is not a storage question: it
    // would happen the same on the fastest disk.
    //
    // IT ALSO REMOVES AN ENORMOUS I/O COST, which is a separate finding. The
    // 6-man set is 160 GB against 0.98 GB for everything up to 5 men, and
    // probing it costs 4.4x the speed (211k nps against 923k on the same
    // position). On a mechanical drive that is fatal - a fixed-node-free SPRT
    // lost 37 of 95 games ON TIME with tables against ZERO without them.
    //
    // WHAT IS STILL OPEN, stated so nobody reads more into this than it says:
    // even the small tables measured -20.2 Elo [-40.1, -0.5] against no tables
    // at all at 10+0.1, over 464 games. That says tablebases may not be worth
    // their probe cost at fast time controls AT ALL, but 10+0.1 is faster than
    // anything the bots play, so dropping them entirely needs a measurement at a
    // representative time control before it is done.
    public int SyzygyProbeLimit { get; private set; } = 7;
    public bool Syzygy50MoveRule { get; private set; } = true;

    // Prints the option declarations the GUI expects right after "id".
    public void Print(TextWriter output)
    {
        output.WriteLine("option name Hash type spin default 64 min 1 max 1024");
        output.WriteLine("option name Threads type spin default 1 min 1 max 32");
        output.WriteLine("option name MoveOverhead type spin default 30 min 0 max 5000");
        output.WriteLine("option name Ponder type check default false");
        output.WriteLine("option name UseNNUE type check default false");
        output.WriteLine("option name EvalFile type string default <empty>");
        output.WriteLine("option name Profile type combo default Default var Default var Bullet var WideWindow var EarlyLmr");
        output.WriteLine("option name Optimism type check default false");
        output.WriteLine("option name NmpEvalGate type check default false");
        output.WriteLine("option name PruningLadder type check default false");
        output.WriteLine("option name PruningLadderFutility type check default true");
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
                Threads = Math.Clamp(threads, 1, 32); // Lazy SMP parallel search.
                return "Threads";

            case "moveoverhead" when int.TryParse(value, out int overhead):
                MoveOverhead = Math.Clamp(overhead, 0, 5000);
                return "MoveOverhead";

            case "ponder" when bool.TryParse(value, out bool ponder):
                Ponder = ponder; // The GUI drives pondering; we just declare support.
                return "Ponder";

            case "usennue" when bool.TryParse(value, out bool useNnue):
                UseNnue = useNnue;
                UseNnueExplicitlySet = true;
                return "UseNNUE";

            case "evalfile":
                EvalFile = value == "<empty>" ? "" : value;
                return "EvalFile";

            case "profile":
                // The known names are listed ONCE. The previous version tested
                // for "Bullet" and mapped everything else to "Default", so a
                // profile added to EngineProfile but not here was accepted by
                // the parser and then silently ignored - two arms of an SPRT
                // selecting different profiles would have played identical
                // chess and reported a perfect draw as if it were a result.
                // Caught by a positive control, not by a test.
                Profile = KnownProfiles.FirstOrDefault(
                    p => p.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? "Default";
                return "Profile";

            case "optimism" when bool.TryParse(value, out bool optimism):
                Optimism = optimism;
                return "Optimism";

            case "nmpevalgate" when bool.TryParse(value, out bool nmpGate):
                NmpEvalGate = nmpGate;
                return "NmpEvalGate";

            case "pruningladder" when bool.TryParse(value, out bool ladder):
                PruningLadder = ladder;
                return "PruningLadder";

            case "pruningladderfutility" when bool.TryParse(value, out bool ladFut):
                PruningLadderFutility = ladFut;
                return "PruningLadderFutility";

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
