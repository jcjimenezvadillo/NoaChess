namespace NoaChess.Engine.Search;

// Search constants exposed for a JOINT retune (SPSA). Every one of them was a
// literal in the search, ported from the reference under this project's unit
// rules (values x0.48, margins raw, history thresholds measured at the
// consumer) or hand-measured here, and each was settled one at a time against
// the others as they stood. Dozens of one-at-a-time port SPRTs have since
// failed, which is what a set of constants sitting at a joint local optimum
// looks like from the inside; moving them together is the lever that is left.
//
// The defaults are the result of the 2026-10-05 SPSA run: 45 parameters tuned
// together over 40,000 games at 25k nodes per move (5,000 iterations), each
// default the run's final value rounded to the nearest integer. Against the
// previous defaults (the literals these fields replaced, kept as "was N" on
// each line) that set passed a fixed-node SPRT at 100k nodes: +16.4 +/- 12.2
// Elo over 1,231 games, H1. With every "was" value set over UCI the engine
// searches exactly as 5.9.26 did. They are statics rather than per-search
// fields: every Lazy SMP helper reads the same values with nothing to copy,
// and UCI only sets options between searches, never while one runs.
//
// Exposed over UCI as "Tune_<Name>" spin options (UciOptions). The ranges bound
// what a tuner may try; they are not a claim about where the optimum lies.
// Divisors keep a minimum above zero. Fixed-point forms (LmpScale, SeMargin,
// the history scales, AspGrowth) exist so an integer tuner has room to move a
// constant that was a small whole number, and each reproduces the old
// arithmetic exactly at its pre-tune value.
public static class SearchParams
{
    // ---- Reverse futility pruning: eval - margin * depth (+ RfpImproving
    // when improving) - parentStatScore / RfpStatDiv >= beta, depth <= RfpDepth.
    // RfpImproving equal to RfpMargin is the old margin x (depth - improving).
    public static int RfpDepth = 6;
    public static int RfpMargin = 78;   // was 85
    public static int RfpImproving = 75;   // was 85
    public static int RfpStatDiv = 177;   // was 180; reference 303 / 0.48 x 0.28

    // ---- Null move: tried from NmpMinDepth, child depth
    // depth - (NmpBase + depth / NmpDepthDiv + min((eval - beta) / NmpEvalDiv, 3)).
    public static int NmpMinDepth = 4;   // was 3
    public static int NmpBase = 4;   // was 3
    public static int NmpDepthDiv = 3;   // was 4
    public static int NmpEvalDiv = 84;   // was 81; reference 168 x 0.48

    // ---- ProbCut: beta + ProbCutMargin - ProbCutImproving when improving;
    // the TT-only form returns at beta + SmallProbCutMargin.
    public static int ProbCutMargin = 151;   // was 150
    public static int ProbCutImproving = 45;   // was 40
    public static int SmallProbCutMargin = 427;   // was 428

    // ---- Internal iterative reduction: from this depth without a TT move.
    public static int IirDepth = 4;

    // ---- Singular extension, the shipped block (SingularTight off): from
    // SeDepth, singularBeta = ttScore - SeMargin * depth / 16 (32 = 2 * depth).
    public static int SeDepth = 7;   // was 8
    public static int SeMargin = 33;   // was 32

    // ---- Late move pruning: LmpBase + depth^2 * LmpScale / 16 quiets,
    // halved when not improving (16 = the old 3 + depth^2).
    public static int LmpBase = 3;
    public static int LmpScale = 16;

    // ---- SEE pruning of quiets: depth <= QuietSeeDepth and
    // SEE < -QuietSeeMargin * depth^2.
    public static int QuietSeeDepth = 8;
    public static int QuietSeeMargin = 17;   // was 23

    // ---- Futility pruning of quiets: depth <= FutilityDepth and
    // eval + FutilityBase + FutilityMargin * depth <= alpha.
    public static int FutilityBase = -3;   // was 0
    public static int FutilityMargin = 90;   // was 100
    public static int FutilityDepth = 4;

    // ---- SEE pruning of captures: depth <= CapSeeDepth and the capture loses
    // at least CapSeeMargin. Not below zero: LosesAtLeast needs a threshold
    // of zero or more.
    public static int CapSeeDepth = 2;
    public static int CapSeeMargin = 95;   // was 100

    // ---- Late move reductions, all but the table in 1024ths of a ply. The
    // table is LmrBase / 100 + ln(depth) ln(move) / (LmrDiv / 100), rebuilt
    // whenever either changes. LmrMinMoves replaces the Default profile's
    // LmrMinMoves (the other profiles keep theirs).
    public static int LmrBase = 83;   // was 75
    public static int LmrDiv = 228;   // was 225
    public static int LmrMinMoves = 4;
    public static int LmrNonPv = 955;   // was 1024
    public static int LmrHistWeight = 1615;   // was 1568; statScore * this / 4096
    public static int LmrKiller = 1044;   // was 1024
    public static int LmrTtCapture = 1077;   // was 1079
    public static int LmrTtPv = 859;   // was 1024
    public static int LmrNotImproving = 1013;   // was 1024

    // ---- Aspiration window: AspWindow replaces the Default profile's
    // AspirationWindow; a failed window grows to window * AspGrowth / 64
    // (128 = the old doubling).
    public static int AspWindow = 38;   // was 50
    public static int AspGrowth = 130;   // was 128

    // ---- Quiescence: futility base stand-pat + QsFutilityMargin (reference
    // 306 x0.48), captures losing at least QsSeeThreshold skipped (-74 x0.48).
    public static int QsFutilityMargin = 145;   // was 147
    public static int QsSeeThreshold = 37;   // was 36

    // ---- History updates at a quiet cutoff, in 64ths of depth^2 (64 = the
    // old depth^2): butterfly bonus and malus, continuation bonus and malus.
    public static int HistBonus = 63;   // was 64
    public static int HistMalus = 66;   // was 64
    public static int ContHistBonus = 62;   // was 64
    public static int ContHistMalus = 66;   // was 64

    // ---- Correction history read weights over a fixed 8192 (= 128 * the
    // table's scale 64): pawn 128 and every other table 32 is the old
    // 4 : 1 blend over 4. Minor and major stay at 32.
    public static int CorrPawnW = 128;
    public static int CorrNonPawnW = 40;   // was 32
    public static int CorrContW = 34;   // was 32

    // ---- Move ordering priors for quiets: the newer killer, the counter move.
    // The older killer gets three quarters of KillerBonus (2954 at 3939).
    public static int KillerBonus = 3939;   // was 4096
    public static int CounterMoveBonus = 2003;   // was 2048

    public sealed record Param(string Name, int Default, int Min, int Max,
                               Func<int> Get, Action<int> Set);

    // The UCI declaration order. Default is captured from the field
    // initialiser above, so the two can never disagree.
    public static readonly Param[] All =
    [
        new("RfpDepth", RfpDepth, 2, 14, () => RfpDepth, v => RfpDepth = v),
        new("RfpMargin", RfpMargin, 30, 200, () => RfpMargin, v => RfpMargin = v),
        new("RfpImproving", RfpImproving, 0, 200, () => RfpImproving, v => RfpImproving = v),
        new("RfpStatDiv", RfpStatDiv, 60, 720, () => RfpStatDiv, v => RfpStatDiv = v),
        new("NmpMinDepth", NmpMinDepth, 1, 6, () => NmpMinDepth, v => NmpMinDepth = v),
        new("NmpBase", NmpBase, 1, 6, () => NmpBase, v => NmpBase = v),
        new("NmpDepthDiv", NmpDepthDiv, 2, 8, () => NmpDepthDiv, v => NmpDepthDiv = v),
        new("NmpEvalDiv", NmpEvalDiv, 30, 300, () => NmpEvalDiv, v => NmpEvalDiv = v),
        new("ProbCutMargin", ProbCutMargin, 50, 400, () => ProbCutMargin, v => ProbCutMargin = v),
        new("ProbCutImproving", ProbCutImproving, 0, 150, () => ProbCutImproving, v => ProbCutImproving = v),
        new("SmallProbCutMargin", SmallProbCutMargin, 150, 900, () => SmallProbCutMargin, v => SmallProbCutMargin = v),
        new("IirDepth", IirDepth, 2, 10, () => IirDepth, v => IirDepth = v),
        new("SeDepth", SeDepth, 4, 12, () => SeDepth, v => SeDepth = v),
        new("SeMargin", SeMargin, 8, 96, () => SeMargin, v => SeMargin = v),
        new("LmpBase", LmpBase, 0, 10, () => LmpBase, v => LmpBase = v),
        new("LmpScale", LmpScale, 6, 40, () => LmpScale, v => LmpScale = v),
        new("QuietSeeDepth", QuietSeeDepth, 3, 14, () => QuietSeeDepth, v => QuietSeeDepth = v),
        new("QuietSeeMargin", QuietSeeMargin, 5, 80, () => QuietSeeMargin, v => QuietSeeMargin = v),
        new("FutilityBase", FutilityBase, -50, 200, () => FutilityBase, v => FutilityBase = v),
        new("FutilityMargin", FutilityMargin, 40, 250, () => FutilityMargin, v => FutilityMargin = v),
        new("FutilityDepth", FutilityDepth, 2, 10, () => FutilityDepth, v => FutilityDepth = v),
        new("CapSeeDepth", CapSeeDepth, 1, 6, () => CapSeeDepth, v => CapSeeDepth = v),
        new("CapSeeMargin", CapSeeMargin, 0, 300, () => CapSeeMargin, v => CapSeeMargin = v),
        new("LmrBase", LmrBase, 0, 200, () => LmrBase,
            v => { LmrBase = v; AlphaBetaSearch.RebuildLmrTable(); }),
        new("LmrDiv", LmrDiv, 150, 450, () => LmrDiv,
            v => { LmrDiv = v; AlphaBetaSearch.RebuildLmrTable(); }),
        new("LmrMinMoves", LmrMinMoves, 1, 8, () => LmrMinMoves, v => LmrMinMoves = v),
        new("LmrNonPv", LmrNonPv, 0, 2048, () => LmrNonPv, v => LmrNonPv = v),
        new("LmrHistWeight", LmrHistWeight, 0, 4096, () => LmrHistWeight, v => LmrHistWeight = v),
        new("LmrKiller", LmrKiller, 0, 2048, () => LmrKiller, v => LmrKiller = v),
        new("LmrTtCapture", LmrTtCapture, 0, 2048, () => LmrTtCapture, v => LmrTtCapture = v),
        new("LmrTtPv", LmrTtPv, 0, 2048, () => LmrTtPv, v => LmrTtPv = v),
        new("LmrNotImproving", LmrNotImproving, 0, 2048, () => LmrNotImproving, v => LmrNotImproving = v),
        new("AspWindow", AspWindow, 10, 150, () => AspWindow, v => AspWindow = v),
        new("AspGrowth", AspGrowth, 80, 256, () => AspGrowth, v => AspGrowth = v),
        new("QsFutilityMargin", QsFutilityMargin, 50, 350, () => QsFutilityMargin, v => QsFutilityMargin = v),
        new("QsSeeThreshold", QsSeeThreshold, 0, 150, () => QsSeeThreshold, v => QsSeeThreshold = v),
        new("HistBonus", HistBonus, 16, 192, () => HistBonus, v => HistBonus = v),
        new("HistMalus", HistMalus, 16, 192, () => HistMalus, v => HistMalus = v),
        new("ContHistBonus", ContHistBonus, 16, 192, () => ContHistBonus, v => ContHistBonus = v),
        new("ContHistMalus", ContHistMalus, 16, 192, () => ContHistMalus, v => ContHistMalus = v),
        new("CorrPawnW", CorrPawnW, 32, 256, () => CorrPawnW, v => CorrPawnW = v),
        new("CorrNonPawnW", CorrNonPawnW, 0, 96, () => CorrNonPawnW, v => CorrNonPawnW = v),
        new("CorrContW", CorrContW, 0, 96, () => CorrContW, v => CorrContW = v),
        new("KillerBonus", KillerBonus, 0, 12288, () => KillerBonus, v => KillerBonus = v),
        new("CounterMoveBonus", CounterMoveBonus, 0, 8192, () => CounterMoveBonus, v => CounterMoveBonus = v),
    ];

    // The parameter called name (case-insensitive, without the "Tune_"
    // prefix), or null.
    public static Param? Find(string name)
    {
        foreach (Param p in All)
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    // Sets a parameter, clamped to its range. False for an unknown name.
    public static bool TrySet(string name, int value)
    {
        Param? p = Find(name);
        if (p is null)
            return false;
        p.Set(Math.Clamp(value, p.Min, p.Max));
        return true;
    }

    public static void ResetToDefaults()
    {
        foreach (Param p in All)
            p.Set(p.Default);
    }
}
