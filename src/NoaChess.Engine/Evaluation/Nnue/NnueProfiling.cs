namespace NoaChess.Engine.Evaluation.Nnue;

// Operation counters for the NNUE cost breakdown (v4.0.0 foundation gate).
//
// WHY THIS EXISTS: NnueInference used to assert that the L1 dot product was
// "THE cost of NNUE eval". At FT=128 / L1=32 that product is 32 x 256 = 8,192
// int16 MACs, roughly 512 AVX2 instructions per evaluation - far too small to
// dominate anything at 446k NPS. The v4.2.0 decision to widen the feature
// transformer must not be taken against an unmeasured cost model a second
// time, so the cost is now measured instead of assumed.
//
// Counting is OFF by default. When off, every Count* call is a single static
// bool test that the JIT hoists out of the hot loops, so normal play is
// unaffected; the `nnueprofile` UCI command turns it on for one search only.
// Counters are plain non-volatile fields incremented without synchronisation:
// a profiling run is single-threaded by construction (the command forces
// Threads=1), and exactness matters less than the ratio.
public static class NnueProfiling
{
    // Set by the `nnueprofile` command around a single instrumented search.
    public static bool Enabled;

    public static long Evaluations;
    public static long AccumulatorUpdates;   // AddFeature + SubtractFeature calls
    public static long FusedMoves;           // MoveFeature calls (one fused pass)
    public static long CopyFromCalls;        // per-ply accumulator duplication
    public static long RefreshesTotal;       // king-move perspective refreshes
    public static long RefreshesFromCache;   // served by the accumulator cache
    public static long RefreshFeaturesTouched; // rows actually added/removed on refresh

    public static void Reset()
    {
        Evaluations = 0;
        AccumulatorUpdates = 0;
        FusedMoves = 0;
        CopyFromCalls = 0;
        RefreshesTotal = 0;
        RefreshesFromCache = 0;
        RefreshFeaturesTouched = 0;
    }

    public static void CountEvaluation() { if (Enabled) Evaluations++; }
    public static void CountAccumulatorUpdate() { if (Enabled) AccumulatorUpdates++; }
    public static void CountFusedMove() { if (Enabled) FusedMoves++; }
    public static void CountCopyFrom() { if (Enabled) CopyFromCalls++; }

    public static void CountRefresh(bool fromCache, int featuresTouched)
    {
        if (!Enabled)
            return;
        RefreshesTotal++;
        if (fromCache)
            RefreshesFromCache++;
        RefreshFeaturesTouched += featuresTouched;
    }
}
