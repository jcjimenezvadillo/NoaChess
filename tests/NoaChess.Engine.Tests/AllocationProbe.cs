using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using NoaChess.Engine.Search;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// How many bytes a search allocates on its own thread. The search is designed
// to allocate nothing per node - every list, stack and scratch buffer is
// preallocated - and a profile once charged 4% of search time to GC polls, so
// the claim is measured here rather than repeated.
//
// MEASURED 2026-09-06: after the warm-up below, a depth-14 search over 745,285
// nodes allocated 0 bytes. The only allocations a cold search makes are the
// undo stack's own growth as it first reaches each depth (a few KB, once per
// process), which is what the warm-up pays for.
public class AllocationProbe(ITestOutputHelper output)
{
    private static string? EmbeddedModelPath()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "src", "NoaChess.UCI", "Resources", "noa-embedded.noannue");
            if (File.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null)
                break;
            dir = parent;
        }
        return null;
    }

    [Fact]
    public void SearchAllocatesNothingPerNode()
    {
        string? model = EmbeddedModelPath();
        if (model is null)
        {
            output.WriteLine("no embedded model found; probe skipped");
            return;
        }
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);

        var search = new AlphaBetaSearch(new NnueEvaluator(net!));
        var board = new Board("r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8");

        // Warm-up: the undo stack grows to the depth the search will reach,
        // lazily created buffers exist and the JIT has run. None of that is a
        // per-node cost, so none of it may count.
        search.FindBestMove(board, SearchLimits.Depth(14));

        long before = GC.GetAllocatedBytesForCurrentThread();
        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(14));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"depth 14: {result.NodesSearched:N0} nodes, {allocated:N0} bytes allocated");
        Assert.True(allocated < 4096,
            $"the search allocated {allocated:N0} bytes over {result.NodesSearched:N0} nodes");
    }
}
