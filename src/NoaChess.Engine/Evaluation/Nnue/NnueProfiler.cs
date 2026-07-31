using System.Diagnostics;
using System.Text;
using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Nnue;

// Measures where NNUE evaluation time actually goes. This is the v4.0.0
// foundation gate: the decision to widen the feature transformer in v4.2.0
// must rest on a measurement, because the previous decision NOT to widen it
// rested on an assertion that does not survive arithmetic (see NnueInference).
//
// METHOD. Two independent measurements, combined:
//   1. Per-primitive cost, timed in isolation over many repetitions with the
//      result consumed so nothing is optimised away.
//   2. Per-primitive call counts, taken from a real fixed-depth search with
//      NnueProfiling.Enabled on.
// Multiplying one by the other attributes total time across the primitives.
// Neither half is trustworthy alone: isolated timings ignore cache state, and
// counts alone say nothing about cost.
//
// The attribution is deliberately reported as a share of the SUM of attributed
// costs, not of wall-clock search time — search also does move generation,
// make/unmake and table probing, and pretending otherwise would overstate the
// eval's share. The interesting number is the ratio BETWEEN the NNUE
// primitives, which is what decides whether width is affordable.
public static class NnueProfiler
{
    // Positions spanning opening, middlegame and endgame, so the piece counts
    // that drive refresh cost are representative rather than flattering.
    private static readonly string[] ProfilePositions =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "r1bqkb1r/pp2nppp/3p4/2pNN1B1/2BnP3/3P4/PPP2PPP/R2bK2R w KQkq - 0 1",
        "8/8/8/8/8/4k3/4p3/4K3 w - - 0 1",
    ];

    public static string Run(NnueNetwork network, Func<Board, int, long> searchToDepth, int depth)
    {
        var report = new StringBuilder();
        bool simd = NnueInference.SimdAvailable;

        report.AppendLine($"nnueprofile: arch={network.ArchitectureId} "
                        + $"({(network.UsesInt8L1 ? "int8" : "int16")} L1) "
                        + $"ft={network.FtOutputs} l1={network.L1Outputs} "
                        + $"qa={network.QA} qb={network.QB} simd={simd}");

        long l1Bytes = (long)network.L1Outputs * 2 * network.FtOutputs
                     * (network.UsesInt8L1 ? 1 : 2);
        long ftBytes = (long)network.FtInputs * network.FtOutputs * 2;
        report.AppendLine($"  weight memory: ft {ftBytes / 1024.0 / 1024.0:F2} MB, "
                        + $"l1 {l1Bytes / 1024.0:F1} KB "
                        + $"(ft rows are {network.FtOutputs * 2} bytes each, "
                        + $"read by feature index)");

        // ---- 1. Primitive costs, isolated ----
        report.AppendLine("--- primitive cost (isolated) ---");

        var board = new Board();
        Fen.Load(board, ProfilePositions[1]);

        double nsMove = TimeMoveFeature(network, board);
        double nsAddSub = TimeAddSubFeature(network, board);
        double nsCopy = TimeCopyFrom(network);
        double nsRefreshCold = TimeRefreshCold(network, board);
        double nsRefreshCached = TimeRefreshCached(network, board);
        double nsEval = TimeEvaluate(network, board);

        report.AppendLine($"  MoveFeature (fused add+sub)   : {nsMove,9:F1} ns");
        report.AppendLine($"  Add/SubtractFeature (one row) : {nsAddSub,9:F1} ns");
        report.AppendLine($"  CopyFrom (both perspectives)  : {nsCopy,9:F1} ns");
        report.AppendLine($"  Refresh COLD (full rebuild)   : {nsRefreshCold,9:F1} ns");
        report.AppendLine($"  Refresh CACHED (finny diff)   : {nsRefreshCached,9:F1} ns"
                        + $"   [{nsRefreshCold / Math.Max(nsRefreshCached, 1e-9):F1}x cheaper]");
        report.AppendLine($"  Evaluate (L1 dot + output)    : {nsEval,9:F1} ns");

        // ---- 2. Call counts from a real search ----
        report.AppendLine($"--- real search (depth {depth}, Threads=1) ---");

        NnueProfiling.Reset();
        NnueProfiling.Enabled = true;
        var sw = Stopwatch.StartNew();
        long nodes = 0;
        foreach (string fen in ProfilePositions)
        {
            var b = new Board();
            Fen.Load(b, fen);
            nodes += searchToDepth(b, depth);
        }
        sw.Stop();
        NnueProfiling.Enabled = false;

        long evaluations = NnueProfiling.Evaluations;
        long fused = NnueProfiling.FusedMoves;
        long addSub = NnueProfiling.AccumulatorUpdates;
        long copies = NnueProfiling.CopyFromCalls;
        long refreshes = NnueProfiling.RefreshesTotal;
        long refreshCached = NnueProfiling.RefreshesFromCache;
        long refreshRows = NnueProfiling.RefreshFeaturesTouched;

        report.AppendLine($"  wall time            : {sw.Elapsed.TotalMilliseconds,12:N0} ms");
        report.AppendLine($"  nodes                : {nodes,12:N0}");
        report.AppendLine($"  evaluations          : {evaluations,12:N0}");
        report.AppendLine($"  fused MoveFeature    : {fused,12:N0}");
        report.AppendLine($"  add/sub updates      : {addSub,12:N0}");
        report.AppendLine($"  CopyFrom             : {copies,12:N0}");
        report.AppendLine($"  refreshes            : {refreshes,12:N0}"
                        + (refreshes > 0
                            ? $"   ({100.0 * refreshCached / refreshes:F1}% cached, "
                            + $"{(double)refreshRows / refreshes:F1} rows avg)"
                            : ""));

        // ---- 3. Attribution ----
        report.AppendLine("--- attributed cost (share of NNUE work) ---");

        double msEval = evaluations * nsEval / 1e6;
        double msFused = fused * nsMove / 1e6;
        double msAddSub = addSub * nsAddSub / 1e6;
        double msCopy = copies * nsCopy / 1e6;
        // Refresh rows are charged at the measured per-row cost; the cached
        // path's fixed overhead is small next to the rows it avoids.
        double msRefresh = refreshRows * nsAddSub / 1e6;
        double total = msEval + msFused + msAddSub + msCopy + msRefresh;
        if (total <= 0)
            total = 1;

        void Line(string name, double ms) =>
            report.AppendLine($"  {name,-22}: {ms,9:F1} ms  ({100.0 * ms / total,5:F1}%)");

        Line("L1 dot product (eval)", msEval);
        Line("FT rows: fused moves", msFused);
        Line("FT rows: add/sub", msAddSub);
        Line("FT rows: refresh", msRefresh);
        Line("accumulator CopyFrom", msCopy);
        report.AppendLine($"  {"TOTAL attributed",-22}: {total,9:F1} ms  "
                        + $"({100.0 * total / Math.Max(sw.Elapsed.TotalMilliseconds, 1e-9):F1}% of wall)");

        double ftShare = 100.0 * (msFused + msAddSub + msRefresh + msCopy) / total;
        report.AppendLine($"  => feature-transformer traffic is {ftShare:F1}% of NNUE work; "
                        + $"the L1 dot product is {100.0 * msEval / total:F1}%.");
        report.AppendLine("  Both scale with ft width, so a wider net multiplies BOTH.");

        // MEASURED CAVEAT, v4.0.0 — read this before acting on the numbers above.
        //
        // The attribution multiplies ISOLATED per-op costs by real call counts,
        // and isolated costs are systematically OPTIMISTIC about how much a
        // micro-optimisation is worth. Rewriting the accumulator updates to use
        // ref-based loads made the primitives 1.9x-2.5x faster in isolation and
        // cut the attributed NNUE total by 40%, yet end-to-end wall time did not
        // move at all (730-767 ms across six alternating runs of both builds,
        // fully overlapping, with node counts byte-identical at 193,746).
        //
        // The reason is that in a real search the feature-transformer rows come
        // from a 5.5 MB weight table indexed by feature, i.e. essentially random
        // access that misses L2. The cost is MEMORY LATENCY, not instruction
        // count, and removing bounds checks does not make DRAM faster. The same
        // caveat applies to the accumulator cache: it makes a refresh 40-50x
        // cheaper per call, but refreshes are only ~6-7% of NNUE work at
        // ft=128, so the end-to-end effect is below noise here.
        //
        // What this means for v4.2.0: treat these shares as a MAP OF WHERE THE
        // WORK IS, not as a promise of what an optimisation will return. The
        // width decision must be made on measured NPS at each width, because
        // whether wider rows help (better spatial locality per row) or hurt
        // (more total traffic) is a memory-system question that cannot be
        // settled from this table.
        report.AppendLine("  CAVEAT: these shares come from isolated per-op costs x real call");
        report.AppendLine("  counts. Isolated costs OVERSTATE optimisation value — the real");
        report.AppendLine("  bottleneck is memory latency on ft rows (random access into a");
        report.AppendLine($"  {ftBytes / 1024.0 / 1024.0:F1} MB table), not instruction count. Use this as a map of");
        report.AppendLine("  where the work is; decide width on measured NPS, never on this table.");

        return report.ToString();
    }

    // ---- Isolated timers ----
    //
    // Each returns nanoseconds per operation. The pattern is the same: warm up,
    // then run enough repetitions that Stopwatch granularity is irrelevant, and
    // consume a value derived from the work so the JIT cannot elide it.

    private const int WarmupIterations = 20_000;
    private const int MeasureIterations = 400_000;

    private static double TimeMoveFeature(NnueNetwork net, Board board)
    {
        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        int a = NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Knight, 18);
        int b = NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Knight, 35);

        for (int i = 0; i < WarmupIterations; i++)
            acc.MoveFeature(net, Color.White, a, b);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < MeasureIterations; i++)
            acc.MoveFeature(net, Color.White, a, b);
        sw.Stop();

        Consume(acc.Values[0][0]);
        return sw.Elapsed.TotalMilliseconds * 1e6 / MeasureIterations;
    }

    private static double TimeAddSubFeature(NnueNetwork net, Board board)
    {
        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        int f = NnueFeatureIndex.Index(Color.White, 4, Color.White, PieceType.Knight, 18);

        for (int i = 0; i < WarmupIterations; i++)
        {
            acc.AddFeature(net, Color.White, f);
            acc.SubtractFeature(net, Color.White, f);
        }

        int pairs = MeasureIterations / 2;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < pairs; i++)
        {
            acc.AddFeature(net, Color.White, f);
            acc.SubtractFeature(net, Color.White, f);
        }
        sw.Stop();

        Consume(acc.Values[0][0]);
        return sw.Elapsed.TotalMilliseconds * 1e6 / (pairs * 2);
    }

    private static double TimeCopyFrom(NnueNetwork net)
    {
        var source = new NnueAccumulator(net.FtOutputs);
        var target = new NnueAccumulator(net.FtOutputs);

        for (int i = 0; i < WarmupIterations; i++)
            target.CopyFrom(source);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < MeasureIterations; i++)
            target.CopyFrom(source);
        sw.Stop();

        Consume(target.Values[0][0]);
        return sw.Elapsed.TotalMilliseconds * 1e6 / MeasureIterations;
    }

    // Full rebuild from the bias — the pre-v4.0.0 king-move path.
    private static double TimeRefreshCold(NnueNetwork net, Board board)
    {
        var acc = new NnueAccumulator(net.FtOutputs);

        for (int i = 0; i < 2_000; i++)
            acc.Refresh(net, board, Color.White);

        const int iterations = 40_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            acc.Refresh(net, board, Color.White);
        sw.Stop();

        Consume(acc.Values[0][0]);
        return sw.Elapsed.TotalMilliseconds * 1e6 / iterations;
    }

    // Cache-served refresh where the position has not changed since the entry
    // was written: the diff is empty, which is the best case and the common one
    // when a king shuffles back and forth.
    private static double TimeRefreshCached(NnueNetwork net, Board board)
    {
        var cache = new NnueAccumulatorCache(net);
        var acc = new NnueAccumulator(net.FtOutputs);

        for (int i = 0; i < 2_000; i++)
            cache.Refresh(acc, board, Color.White);

        const int iterations = 40_000;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            cache.Refresh(acc, board, Color.White);
        sw.Stop();

        Consume(acc.Values[0][0]);
        return sw.Elapsed.TotalMilliseconds * 1e6 / iterations;
    }

    private static double TimeEvaluate(NnueNetwork net, Board board)
    {
        var acc = new NnueAccumulator(net.FtOutputs);
        acc.Refresh(net, board, Color.White);
        acc.Refresh(net, board, Color.Black);
        short[] stm = acc.Values[0];
        short[] opp = acc.Values[1];

        bool wasEnabled = NnueProfiling.Enabled;
        NnueProfiling.Enabled = false;

        int sink = 0;
        for (int i = 0; i < WarmupIterations; i++)
            sink ^= NnueInference.Evaluate(net, stm, opp);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < MeasureIterations; i++)
            sink ^= NnueInference.Evaluate(net, stm, opp);
        sw.Stop();

        NnueProfiling.Enabled = wasEnabled;
        Consume(sink);
        return sw.Elapsed.TotalMilliseconds * 1e6 / MeasureIterations;
    }

    // Keeps the optimiser honest without printing anything.
    private static int _sink;
    private static void Consume(int value) => _sink ^= value;
}
