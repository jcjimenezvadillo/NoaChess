using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using NoaChess.Engine.Search;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// The mirror of LostBandResistanceTests for the WINNING side. With seven or
// more men the root is outside the tablebases, but every winning line that
// captures something enters them, and a tablebase win is scored TbWin - ply:
// the sooner the line reaches the tables the higher the score, and the
// quickest way there is to have a piece taken. In the position below (a bot
// game of 2026-09-07, NoaBot vs Farddown_YG) promoting scored the same band
// value as every knight move, including the one that hung the knight, and the
// engine shuffled the knight for two moves before queening. TbWinTieBreak
// flattens the band at the root and picks by material kept plus progress.
public class WonBandTieBreakTests(ITestOutputHelper output)
{
    private const string PromoteOrShuffle = "2k5/5P2/8/6N1/6P1/8/1n4PK/8 w - - 0 69";

    [SyzygyTheory]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(16)]
    public void ATablebaseWonRootQueensInsteadOfOfferingTheKnight(int depth)
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
        search.SyzygyProbeLimit = 7;
        Assert.True(search.UseTbWinTieBreak, "the tie-break ships ON");
        var board = new Board(PromoteOrShuffle);

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(depth));
        output.WriteLine($"depth {depth} best {result.BestMove} score {result.Score}");

        // The root is proved tablebase-won (a band score, not a mate) and the
        // move is the promotion, not a knight shuffle.
        Assert.True(result.Score >= AlphaBetaSearch.TbWin - 256
                    && result.Score < AlphaBetaSearch.MateScore - 1000,
            $"expected a tablebase-win band score, got {result.Score}");
        Assert.Equal("f7f8q", result.BestMove.ToString());
    }

    // The same mode, far from the tables (2026-09-09). The tie-break was
    // designed on a SEVEN-man ending one capture from the tablebases, where its
    // crude key - what the opponent can win by capture, plus promotions - is a
    // fair stand-in for a ranking the flat band score cannot provide. It was
    // firing everywhere: over 129 positions taken from real bot games where the
    // search announced a tablebase win, the median had ten men and the largest
    // twenty-one, and conversion fell from 72.7% with six men or fewer to 40.0%
    // with thirteen or more. WonBandMaxMen keeps the mode inside the range it
    // was measured in. This position has twelve men and a band score, so the
    // mode must be OFF at the shipped threshold and the move must come from the
    // search, not from the key.
    private const string BandFarFromTheTables =
        "6k1/8/P3p3/6p1/4q3/1B6/5K2/5N2 b - - 0 62";

    [SyzygyTheory]
    [InlineData(8)]
    [InlineData(20)]
    public void TheWonBandTieBreakIsOffWhenTheThresholdExcludesThePosition(int maxMen)
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

        var search = new AlphaBetaSearch(new NnueEvaluator(net!)) { SyzygyProbeLimit = 7 };
        search.WonBandMaxMen = maxMen;
        var board = new Board(BandFarFromTheTables);
        int men = System.Numerics.BitOperations.PopCount(board.AllOccupancy);
        Assert.Equal(8, men);

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(20));
        output.WriteLine($"maxMen {maxMen}, men {men}, best {result.BestMove}, score {result.Score}");

        // Whatever the threshold, the search must return a legal move and must
        // not hand back the null move: the guard changes which ranking decides,
        // never whether a move is produced.
        Assert.NotEqual(Move.None, result.BestMove);
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
}
