using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// A single-threaded search of the same position after a reset must produce
// the same tree, node for node, in the DETERMINISTIC configuration
// (QsStackMove on). The shipped default keeps quiescence keyed the measured
// way, which reads stack slots the previous search left behind and is
// therefore not deterministic across searches; this test pins the
// configuration the determinism repair provides, so it cannot regress while
// its Elo is being measured.
public class SearchDeterminismTests
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
            if (parent is null) break;
            dir = parent;
        }
        return null;
    }

    [Theory]
    [InlineData("r2q1rk1/ppp1nppp/2n1p3/3p4/2PP4/2NQPN2/PP3PPP/R3K2R b KQ - 0 9")]
    [InlineData("1r1n3k/1pq1rpp1/pR1Np2p/2P5/8/P1Q1P2P/5PP1/1R4K1 b - - 0 33")]
    [InlineData("8/8/4k3/8/2p5/8/B2P2K1/8 w - - 0 1")]
    public void TheSameSearchAfterAResetVisitsTheSameNodes(string fen)
    {
        string? model = EmbeddedModelPath();
        if (model is null)
            return;
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);
        var search = new AlphaBetaSearch(new NnueEvaluator(net!))
        {
            UseQsStackMove = true,
            UseQsContCorrection = false,
        };

        long[] nodes = new long[3];
        Move[] moves = new Move[3];
        for (int run = 0; run < 3; run++)
        {
            search.Reset();
            SearchResult result = search.FindBestMove(new Board(fen), SearchLimits.Depth(11));
            nodes[run] = result.NodesSearched;
            moves[run] = result.BestMove;
        }

        Assert.True(nodes[0] == nodes[1] && nodes[1] == nodes[2],
            $"{fen}: {nodes[0]}, {nodes[1]}, {nodes[2]} nodes over three identical searches");
        Assert.Equal(moves[0], moves[2]);
    }
}
