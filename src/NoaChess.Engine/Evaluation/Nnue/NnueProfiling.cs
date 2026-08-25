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
    public static long CopyFromCalls;        // both-perspective duplication
    public static long RefreshesTotal;       // king-move perspective refreshes
    public static long RefreshesFromCache;   // served by the accumulator cache
    public static long RefreshFeaturesTouched; // rows actually added/removed on refresh

    // Lazy accumulator (v4.5.0). Pushes counts the work the EAGER stack would
    // have done; PendingApplied counts what the lazy one actually did. The gap
    // between them is the saving, and PerspectiveCopies is what replaced the
    // per-ply CopyFrom.
    public static long Pushes;               // PushMove + PushNull calls
    public static long PendingApplied;       // recorded updates actually materialised
    public static long PerspectiveCopies;    // single-perspective copies from an ancestor

    public static void Reset()
    {
        Evaluations = 0;
        AccumulatorUpdates = 0;
        FusedMoves = 0;
        CopyFromCalls = 0;
        RefreshesTotal = 0;
        RefreshesFromCache = 0;
        RefreshFeaturesTouched = 0;
        Pushes = 0;
        PendingApplied = 0;
        PerspectiveCopies = 0;
        ResetThreatFinny();
    }

    public static void CountEvaluation() { if (Enabled) Evaluations++; }
    public static void CountAccumulatorUpdate() { if (Enabled) AccumulatorUpdates++; }
    public static void CountFusedMove() { if (Enabled) FusedMoves++; }
    public static void CountCopyFrom() { if (Enabled) CopyFromCalls++; }
    public static void CountPush() { if (Enabled) Pushes++; }
    public static void CountPendingApplied() { if (Enabled) PendingApplied++; }
    public static void CountPerspectiveCopy() { if (Enabled) PerspectiveCopies++; }

    public static void CountRefresh(bool fromCache, int featuresTouched)
    {
        if (!Enabled)
            return;
        RefreshesTotal++;
        if (fromCache)
            RefreshesFromCache++;
        RefreshFeaturesTouched += featuresTouched;
    }

    // ---- Threat finny-table probe --------------------------------------
    //
    // WHAT IT IS DECIDING. A threat refresh rebuilds from the bias and touches
    // every active threat relation - about 73 random rows in a 21 MB weight
    // table, which is 73 cache misses. HalfKA avoids that with a finny table:
    // it keeps the last accumulator built for each king square and diffs
    // against it instead of rebuilding. Threats have no such table.
    //
    // WHY IT HAS TO BE MEASURED BEFORE IT IS WRITTEN. The HalfKA table diffs
    // BITBOARDS, which is a dozen popcounts. The obvious threat version would
    // diff two ~73-entry FEATURE lists, and that is about 5,300 comparisons
    // against the ~73 cache misses it saves - close enough that arguing about
    // it settles nothing.
    //
    // The version worth having reuses the delta machinery instead: treat the
    // cached entry as just an earlier position, take the squares whose contents
    // differ, and run the existing affected-attackers argument over them. That
    // one scales with the number of DIFFERING SQUARES, not with the list
    // length. So the number that decides the design is how far the cached
    // position sits from the current one, and both are counted here.
    // Threat ROW updates specifically, split out of AccumulatorUpdates: that
    // counter mixes HalfKA and threat rows, and the two live in different
    // tables with different cache behaviour, so a share of the total needs
    // them apart.
    public static long ThreatRowUpdates;
    public static void CountThreatRow() { if (Enabled) ThreatRowUpdates++; }

    public static long ThreatRefreshes;
    public static long ThreatFinnyHits;
    public static long ThreatRowsFull;       // rows a full rebuild touches
    public static long ThreatRowsFullOnHit;  // ... restricted to refreshes a cache would serve
    public static long ThreatRowsChanged;    // rows a diff from the cache would touch
    public static long ThreatSquaresChanged; // how far the cached position is, in squares
    public static long ThreatSquaresWorst;

    // One entry per (perspective, king square). A profiling run is
    // single-threaded by construction, so no synchronisation and no
    // [ThreadStatic].
    private const int FinnySlots = 2 * 64;
    private static byte[]? _finnyBoard;      // 64 squares of piece code per slot
    private static int[]? _finnyFeatures;
    private static int[]? _finnyCount;
    private static bool[]? _finnyValid;

    public static void CountThreatRefresh(int perspective, int kingSquare,
                                          ReadOnlySpan<byte> squares, ReadOnlySpan<int> features)
    {
        if (!Enabled)
            return;

        _finnyBoard ??= new byte[FinnySlots * 64];
        _finnyFeatures ??= new int[FinnySlots * ThreatFeatureIndex.MaxActiveFeatures];
        _finnyCount ??= new int[FinnySlots];
        _finnyValid ??= new bool[FinnySlots];

        int slot = perspective * 64 + kingSquare;
        Span<byte> cachedBoard = _finnyBoard.AsSpan(slot * 64, 64);
        Span<int> cachedFeatures = _finnyFeatures.AsSpan(
            slot * ThreatFeatureIndex.MaxActiveFeatures, ThreatFeatureIndex.MaxActiveFeatures);

        ThreatRefreshes++;
        ThreatRowsFull += features.Length;

        if (_finnyValid[slot])
        {
            ThreatFinnyHits++;
            ThreatRowsFullOnHit += features.Length;

            int differing = 0;
            for (int sq = 0; sq < 64; sq++)
                if (cachedBoard[sq] != squares[sq])
                    differing++;
            ThreatSquaresChanged += differing;
            if (differing > ThreatSquaresWorst)
                ThreatSquaresWorst = differing;

            ReadOnlySpan<int> cached = cachedFeatures[.._finnyCount[slot]];
            int changed = 0;
            for (int i = 0; i < cached.Length; i++)
                if (!Contains(features, cached[i]))
                    changed++;
            for (int i = 0; i < features.Length; i++)
                if (!Contains(cached, features[i]))
                    changed++;
            ThreatRowsChanged += changed;
        }

        squares.CopyTo(cachedBoard);
        features.CopyTo(cachedFeatures);
        _finnyCount[slot] = features.Length;
        _finnyValid[slot] = true;
    }

    private static bool Contains(ReadOnlySpan<int> haystack, int needle)
    {
        for (int i = 0; i < haystack.Length; i++)
            if (haystack[i] == needle)
                return true;
        return false;
    }

    public static void ResetThreatFinny()
    {
        ThreatRowUpdates = 0;
        ThreatRefreshes = 0;
        ThreatFinnyHits = 0;
        ThreatRowsFull = 0;
        ThreatRowsFullOnHit = 0;
        ThreatRowsChanged = 0;
        ThreatSquaresChanged = 0;
        ThreatSquaresWorst = 0;
        if (_finnyValid != null)
            Array.Clear(_finnyValid);
    }
}
