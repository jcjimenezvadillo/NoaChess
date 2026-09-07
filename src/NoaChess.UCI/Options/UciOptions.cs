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
    public bool CorrectionBlend { get; private set; }
    public bool StatScoreLmr { get; private set; } = true;
    public bool NodeTimeFactor { get; private set; }
    public bool EvalStabilityTime { get; private set; }
    public bool RootSafetyNet { get; private set; }
    // Floor on the fresh thinking after a ponderhit (see AlphaBetaSearch.UsePonderMinThink).
    public bool PonderMinThink { get; private set; }
    // Easy-move cut only when winning (see AlphaBetaSearch.UseEasyMoveWinOnly).
    public bool EasyMoveWinOnly { get; private set; } = true;
    // Root static eval on the search stack (see AlphaBetaSearch.UseRootStaticEval).
    public bool RootStaticEval { get; private set; }
    // Quiescence moves recorded on the search stack (see AlphaBetaSearch.UseQsStackMove).
    public bool QsStackMove { get; private set; }
    // Checking quiets exempt from futility (see AlphaBetaSearch.UseCheckExemptFutility).
    public bool CheckExemptFutility { get; private set; }
    // Window clamped to the reachable mate scores (see AlphaBetaSearch.UseMateDistancePruning).
    public bool MateDistancePruning { get; private set; } = true;
    // Transposition cutoff refused at PV nodes (see AlphaBetaSearch.UseTtNoPvCutoff).
    public bool TtNoPvCutoff { get; private set; }
    // Suspend the easy-move cut under fifty-move pressure (see AlphaBetaSearch).
    public bool EasyMoveFiftyGuard { get; private set; } = true;
    // Break ties between equal-scored root moves in drawn positions (see AlphaBetaSearch).
    public bool DrawTieBreak { get; private set; }
    public bool SmpOvershootTaper { get; private set; }
    // The 2026-09-08 audit switches (see AlphaBetaSearch for each one).
    public bool RepetitionAfterRoot { get; private set; }
    public bool NmpNonPvOnly { get; private set; }
    public bool TtEvalRefine { get; private set; }
    public bool TtKeepMoveOnFailLow { get; private set; } = true;
    public bool TtMateReuse { get; private set; }
    public bool RootScoreOrdering { get; private set; }
    public bool Razoring { get; private set; }
    public bool LmpAllDepths { get; private set; }
    public bool QuietSeePrune { get; private set; }
    public bool CaptureSeePruneDeep { get; private set; }
    // Percent multiplier on the clock optimum (see TimeManager.FromClock).
    public int TimeScale { get; private set; } = 100;
    public bool SmpDiversify { get; private set; }
    public bool SmpAspDiversify { get; private set; }
    public bool SmpVoteAll { get; private set; }
    public bool CutNodeLmr { get; private set; }
    public bool FailLowCorrection { get; private set; }
    public bool MoveCountLmr { get; private set; }
    public bool DynamicAspiration { get; private set; }
    public bool HistoryBonus { get; private set; }
    public bool CorrectionLmr { get; private set; }
    public bool KillerShallowing { get; private set; } = true;
    public bool TbPvCap { get; private set; }
    public bool TbResistance { get; private set; } = true;
    // Resistance tie-break in plainly lost roots (see AlphaBetaSearch.UseLostResistance).
    public bool LostResistance { get; private set; }
    public int LostResistanceBound { get; private set; } = 600;
    public bool CaptureLmr { get; private set; }
    public bool NmpPackage { get; private set; }
    // Convert a pondered search in place on "ponderhit" instead of relaunching
    // it over the warm table. Affects nothing unless the GUI actually ponders.
    // MEASURED -23.7 [-41.2, -6.3] at 60+1 with ponder on both arms, H0 at 382
    // games, with zero time forfeits - it lost on chess, not on the clock.
    // Kept inert; the full record is in AlphaBetaSearch.ApplyClockLimits.
    public bool PonderInPlace { get; private set; }

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
        output.WriteLine("option name CorrectionBlend type check default false");
        output.WriteLine("option name StatScoreLmr type check default true");
        output.WriteLine("option name NodeTimeFactor type check default false");
        output.WriteLine("option name EvalStabilityTime type check default false");
        output.WriteLine("option name RootSafetyNet type check default false");
        output.WriteLine("option name PonderMinThink type check default false");
        output.WriteLine("option name EasyMoveWinOnly type check default true");
        output.WriteLine("option name RootStaticEval type check default false");
        output.WriteLine("option name QsStackMove type check default false");
        output.WriteLine("option name CheckExemptFutility type check default false");
        output.WriteLine("option name MateDistancePruning type check default true");
        output.WriteLine("option name TtNoPvCutoff type check default false");
        output.WriteLine("option name EasyMoveFiftyGuard type check default true");
        output.WriteLine("option name DrawTieBreak type check default false");
        output.WriteLine("option name SmpOvershootTaper type check default false");
        output.WriteLine("option name RepetitionAfterRoot type check default false");
        output.WriteLine("option name NmpNonPvOnly type check default false");
        output.WriteLine("option name TtEvalRefine type check default false");
        output.WriteLine("option name TtKeepMoveOnFailLow type check default true");
        output.WriteLine("option name TtMateReuse type check default false");
        output.WriteLine("option name RootScoreOrdering type check default false");
        output.WriteLine("option name Razoring type check default false");
        output.WriteLine("option name LmpAllDepths type check default false");
        output.WriteLine("option name QuietSeePrune type check default false");
        output.WriteLine("option name CaptureSeePruneDeep type check default false");
        output.WriteLine("option name TimeScale type spin default 100 min 50 max 200");
        output.WriteLine("option name SmpDiversify type check default false");
        output.WriteLine("option name SmpAspDiversify type check default false");
        output.WriteLine("option name SmpVoteAll type check default false");
        output.WriteLine("option name CutNodeLmr type check default false");
        output.WriteLine("option name FailLowCorrection type check default false");
        output.WriteLine("option name MoveCountLmr type check default false");
        output.WriteLine("option name DynamicAspiration type check default false");
        output.WriteLine("option name HistoryBonus type check default false");
        output.WriteLine("option name CorrectionLmr type check default false");
        output.WriteLine("option name KillerShallowing type check default true");
        output.WriteLine("option name TbPvCap type check default false");
        output.WriteLine("option name TbResistance type check default true");
        output.WriteLine("option name LostResistance type check default false");
        output.WriteLine("option name LostResistanceBound type spin default 600 min 100 min 100 max 5000".Replace("min 100 min 100", "min 100"));
        output.WriteLine("option name CaptureLmr type check default false");
        output.WriteLine("option name NmpPackage type check default false");
        output.WriteLine("option name PonderInPlace type check default false");
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
            case "correctionblend" when bool.TryParse(value, out bool corrBlend):
                CorrectionBlend = corrBlend;
                return "CorrectionBlend";
            case "statscorelmr" when bool.TryParse(value, out bool statLmr):
                StatScoreLmr = statLmr;
                return "StatScoreLmr";
            case "nodetimefactor" when bool.TryParse(value, out bool ntf):
                NodeTimeFactor = ntf;
                return "NodeTimeFactor";
            case "evalstabilitytime" when bool.TryParse(value, out bool est):
                EvalStabilityTime = est;
                return "EvalStabilityTime";
            case "rootsafetynet" when bool.TryParse(value, out bool rsn):
                RootSafetyNet = rsn;
                return "RootSafetyNet";
            case "ponderminthink" when bool.TryParse(value, out bool pmt):
                PonderMinThink = pmt;
                return "PonderMinThink";
            case "easymovewinonly" when bool.TryParse(value, out bool emw):
                EasyMoveWinOnly = emw;
                return "EasyMoveWinOnly";
            case "rootstaticeval" when bool.TryParse(value, out bool rse):
                RootStaticEval = rse;
                return "RootStaticEval";
            case "qsstackmove" when bool.TryParse(value, out bool qsm):
                QsStackMove = qsm;
                return "QsStackMove";
            case "checkexemptfutility" when bool.TryParse(value, out bool cef):
                CheckExemptFutility = cef;
                return "CheckExemptFutility";
            case "matedistancepruning" when bool.TryParse(value, out bool mdp):
                MateDistancePruning = mdp;
                return "MateDistancePruning";
            case "ttnopvcutoff" when bool.TryParse(value, out bool tnp):
                TtNoPvCutoff = tnp;
                return "TtNoPvCutoff";
            case "easymovefiftyguard" when bool.TryParse(value, out bool emf):
                EasyMoveFiftyGuard = emf;
                return "EasyMoveFiftyGuard";
            case "drawtiebreak" when bool.TryParse(value, out bool dtb):
                DrawTieBreak = dtb;
                return "DrawTieBreak";
            case "smpovershoottaper" when bool.TryParse(value, out bool sot):
                SmpOvershootTaper = sot;
                return "SmpOvershootTaper";
            case "repetitionafterroot" when bool.TryParse(value, out bool rar):
                RepetitionAfterRoot = rar;
                return "RepetitionAfterRoot";
            case "nmpnonpvonly" when bool.TryParse(value, out bool nnp):
                NmpNonPvOnly = nnp;
                return "NmpNonPvOnly";
            case "ttevalrefine" when bool.TryParse(value, out bool ter):
                TtEvalRefine = ter;
                return "TtEvalRefine";
            case "ttkeepmoveonfaillow" when bool.TryParse(value, out bool tkm):
                TtKeepMoveOnFailLow = tkm;
                return "TtKeepMoveOnFailLow";
            case "ttmatereuse" when bool.TryParse(value, out bool tmr):
                TtMateReuse = tmr;
                return "TtMateReuse";
            case "rootscoreordering" when bool.TryParse(value, out bool rso):
                RootScoreOrdering = rso;
                return "RootScoreOrdering";
            case "razoring" when bool.TryParse(value, out bool rz):
                Razoring = rz;
                return "Razoring";
            case "timescale" when int.TryParse(value, out int ts):
                TimeScale = Math.Clamp(ts, 50, 200);
                return "TimeScale";
            case "lmpalldepths" when bool.TryParse(value, out bool lad):
                LmpAllDepths = lad;
                return "LmpAllDepths";
            case "quietseeprune" when bool.TryParse(value, out bool qsp):
                QuietSeePrune = qsp;
                return "QuietSeePrune";
            case "captureseeprunedeep" when bool.TryParse(value, out bool csp):
                CaptureSeePruneDeep = csp;
                return "CaptureSeePruneDeep";
            case "smpdiversify" when bool.TryParse(value, out bool sdv):
                SmpDiversify = sdv;
                return "SmpDiversify";
            case "smpaspdiversify" when bool.TryParse(value, out bool sad):
                SmpAspDiversify = sad;
                return "SmpAspDiversify";
            case "smpvoteall" when bool.TryParse(value, out bool sva):
                SmpVoteAll = sva;
                return "SmpVoteAll";
            case "cutnodelmr" when bool.TryParse(value, out bool cutLmr):
                CutNodeLmr = cutLmr;
                return "CutNodeLmr";
            case "faillowcorrection" when bool.TryParse(value, out bool flc):
                FailLowCorrection = flc;
                return "FailLowCorrection";
            case "movecountlmr" when bool.TryParse(value, out bool mcl):
                MoveCountLmr = mcl;
                return "MoveCountLmr";
            case "dynamicaspiration" when bool.TryParse(value, out bool dyna):
                DynamicAspiration = dyna;
                return "DynamicAspiration";
            case "historybonus" when bool.TryParse(value, out bool hb):
                HistoryBonus = hb;
                return "HistoryBonus";
            case "correctionlmr" when bool.TryParse(value, out bool clmr):
                CorrectionLmr = clmr;
                return "CorrectionLmr";
            case "killershallowing" when bool.TryParse(value, out bool ks):
                KillerShallowing = ks;
                return "KillerShallowing";
            case "tbpvcap" when bool.TryParse(value, out bool tbc):
                TbPvCap = tbc;
                return "TbPvCap";
            case "tbresistance" when bool.TryParse(value, out bool tbr):
                TbResistance = tbr;
                return "TbResistance";
            case "lostresistance" when bool.TryParse(value, out bool lrs):
                LostResistance = lrs;
                return "LostResistance";
            case "lostresistancebound" when int.TryParse(value, out int lrb):
                LostResistanceBound = Math.Clamp(lrb, 100, 5000);
                return "LostResistanceBound";
            case "capturelmr" when bool.TryParse(value, out bool clm):
                CaptureLmr = clm;
                return "CaptureLmr";
            case "nmppackage" when bool.TryParse(value, out bool nmp):
                NmpPackage = nmp;
                return "NmpPackage";
            case "ponderinplace" when bool.TryParse(value, out bool pip):
                PonderInPlace = pip;
                return "PonderInPlace";

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
