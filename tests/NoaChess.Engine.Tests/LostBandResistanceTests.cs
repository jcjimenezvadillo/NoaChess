using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using NoaChess.Engine.Search;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// A tablebase-lost score is -TbWin + ply: it says how many plies separate the
// line from the tablebase and NOTHING about how well the side resists, so
// inside that band material is free and the engine can hand a rook over for
// nothing. TbResistance exists to restore a gradient at the root, and twice it
// failed to: once because the selection key gave the band's own score
// 4096-to-1 priority over the tie-break, so the tie-break waited for an exact
// tie the band never produces; and once because the key it used was the static
// evaluation, which cannot see the capture the move walks into.
//
// The position below is from a real bot game (2026-09-06, NoaBot vs
// MalanChess). White is lost either way, but Re3 hands the rook to the queen
// and every king move does not.
public class LostBandResistanceTests(ITestOutputHelper output)
{
    private const string LostWithARookToThrow = "8/6r1/1p4P1/6K1/P7/7R/1k4P1/2q5 w - - 0 60";

    // A bare king against king and queen, from a bot game. Every move loses,
    // and the engine used to walk into the fastest mate because the root
    // filter switched the tablebase scores off and left the saturated
    // evaluation to choose. With the probe kept on for a lost root the search
    // sees the mates and picks the longest defence: Kc8, not Ka8.
    private const string BareKingVersusQueen = "8/1k6/8/1Q3K2/8/8/8/8 b - - 3 67";

    [SyzygyFact]
    public void ABareKingTakesTheLongestRoadToMate()
    {
        string? model = EmbeddedModelPath();
        if (model is null)
            return;
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);
        string tb = SyzygyTestEnvironment.TablebasePath!;
        if (Tablebases.Syzygy.CurrentPath != tb)
            Tablebases.Syzygy.Init(tb);

        var search = new AlphaBetaSearch(new NnueEvaluator(net!));
        // The UCI layer sets this after loading the tables; a bare search
        // object probes nothing until it is told how many men the tables hold.
        search.SyzygyProbeLimit = 7;
        var board = new Board(BareKingVersusQueen);
        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(12));
        output.WriteLine($"best {result.BestMove} score {result.Score}");

        // The search must have SEEN the mate, i.e. reported a mate score, and
        // must not have chosen the move that loses fastest.
        Assert.True(result.Score <= -(AlphaBetaSearch.MateScore - 1000),
            $"expected a mate score, got {result.Score}");
        Assert.NotEqual("b7a8", result.BestMove.ToString());
    }

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

    [SyzygyFact]
    public void ALostRootDoesNotHandOverTheRook()
    {
        string? model = EmbeddedModelPath();
        if (model is null)
        {
            output.WriteLine("no embedded model found; probe skipped");
            return;
        }
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);
        string tb = SyzygyTestEnvironment.TablebasePath!;
        if (Tablebases.Syzygy.CurrentPath != tb)
            Tablebases.Syzygy.Init(tb);
        Assert.True(Tablebases.Syzygy.Available);

        var search = new AlphaBetaSearch(new NnueEvaluator(net!));
        var board = new Board(LostWithARookToThrow);

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(16));
        output.WriteLine($"best {result.BestMove} score {result.Score}");

        // The rook interposition is the move under test; any king move keeps it.
        Assert.NotEqual("h3e3", result.BestMove.ToString());

        // And the answer is still legal and real, not a fallback.
        Assert.Contains(MoveGenerator.GenerateLegalMoves(board), m => m == result.BestMove);
    }
}
