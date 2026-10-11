using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;
using NoaChess.UCI.Options;

namespace NoaChess.Engine.Tests;

// The Tune_ options (SearchParams) are statics every search reads, so these
// tests restore the defaults whatever happens; the assembly runs its tests
// serially, so no other test can observe a value while it is changed.
public class SearchParamsTests
{
    private const string Fen = "r2q1rk1/ppp1nppp/2n1p3/3p4/2PP4/2NQPN2/PP3PPP/R3K2R b KQ - 0 9";

    private static long Nodes()
    {
        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        return search.FindBestMove(new Board(Fen), SearchLimits.Depth(7)).NodesSearched;
    }

    [Fact]
    public void EveryParameterIsDeclaredAtItsDefaultAndInRange()
    {
        var output = new StringWriter();
        new UciOptions().Print(output);
        string declared = output.ToString();

        Assert.Equal(SearchParams.All.Length,
                     SearchParams.All.Select(p => p.Name.ToLowerInvariant()).Distinct().Count());
        foreach (SearchParams.Param p in SearchParams.All)
        {
            Assert.Equal(p.Default, p.Get());
            Assert.InRange(p.Default, p.Min, p.Max);
            Assert.Contains($"option name Tune_{p.Name} type spin default {p.Default} min {p.Min} max {p.Max}",
                            declared);
        }
    }

    [Fact]
    public void SetOptionClampsRoundsAndReachesTheField()
    {
        var options = new UciOptions();
        try
        {
            Assert.Equal("Tune_RfpMargin", options.Set("tune_rfpmargin", "120"));
            Assert.Equal(120, SearchParams.RfpMargin);
            Assert.Equal("Tune_RfpMargin", options.Set("Tune_RfpMargin", "99.6"));
            Assert.Equal(100, SearchParams.RfpMargin);
            Assert.Equal("Tune_RfpMargin", options.Set("Tune_RfpMargin", "100000"));
            Assert.Equal(200, SearchParams.RfpMargin);
            Assert.Null(options.Set("Tune_NoSuchThing", "1"));
            Assert.Null(options.Set("Tune_RfpMargin", "abc"));
            Assert.Equal(200, SearchParams.RfpMargin);
        }
        finally
        {
            SearchParams.ResetToDefaults();
        }
        Assert.Equal(78, SearchParams.RfpMargin);
    }

    [Fact]
    public void ChangingAndRestoringTheLmrTableRestoresTheTree()
    {
        long before = Nodes();
        try
        {
            Assert.True(SearchParams.TrySet("LmrDiv", 300));
            Assert.True(SearchParams.TrySet("LmrBase", 120));
            Assert.NotEqual(before, Nodes());
        }
        finally
        {
            SearchParams.ResetToDefaults();
        }
        Assert.Equal(before, Nodes());
    }
}
