using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// The knobs added for SPSA round 7 default to the literal they replaced (or to
// zero, which switches a new feature off), so the tree must be unchanged at the
// defaults and must change when a knob is pushed. The assembly runs its tests
// serially, so the statics can be moved and restored here.
public class Round7KnobTests
{
    private static readonly string[] Fens =
    [
        "r2q1rk1/ppp1nppp/2n1p3/3p4/2PP4/2NQPN2/PP3PPP/R3K2R b KQ - 0 9",
        "r1bq1rk1/pp2ppbp/2np1np1/8/3NP3/2N1BP2/PPPQ2PP/R3KB1R w KQ - 0 9",
        "2rq1rk1/pp1bppbp/3p1np1/n7/2PNP3/1PN1B1P1/P3PPBP/R2Q1RK1 w - - 0 11",
    ];

    private static long Nodes()
    {
        long total = 0;
        foreach (string fen in Fens)
        {
            var search = new AlphaBetaSearch(new ClassicalEvaluator());
            total += search.FindBestMove(new Board(fen), SearchLimits.Depth(8)).NodesSearched;
        }
        return total;
    }

    [Theory]
    [InlineData("EvalDiffScale", 8)]
    [InlineData("RfpOppWorsening", 120)]
    [InlineData("LmrAlphaGap", 12)]
    [InlineData("HistTtMoveBonus", 100)]
    [InlineData("HistTriedScale", 16)]
    [InlineData("CorrPairW", 96)]
    [InlineData("KillerOlderShare", 0)]
    [InlineData("TtCutHistMul", 32)]
    [InlineData("HindsightDownDelta", 0)]
    [InlineData("CorrMinorW", 0)]
    public void PushingAKnobChangesTheTreeAndRestoringItRestoresIt(string name, int pushed)
    {
        long before = Nodes();
        try
        {
            Assert.True(SearchParams.TrySet(name, pushed));
            Assert.NotEqual(before, Nodes());
        }
        finally
        {
            SearchParams.ResetToDefaults();
        }
        Assert.Equal(before, Nodes());
    }

    [Fact]
    public void NewFeaturesAreOffByDefault()
    {
        Assert.Equal(0, SearchParams.EvalDiffScale);
        Assert.Equal(0, SearchParams.EvalDiffCenter);
        Assert.Equal(0, SearchParams.RfpOppWorsening);
        Assert.Equal(0, SearchParams.LmrAlphaGap);
        Assert.Equal(0, SearchParams.HistTtMoveBonus);
        Assert.Equal(0, SearchParams.HistTriedScale);
        Assert.Equal(0, SearchParams.CorrPairW);
        Assert.Equal(512, SearchParams.HistDecay);
    }

    // Shared correction tables and the odd-thread null-move reduction touch only
    // the helpers, so one thread must play exactly the same tree with them on.
    [Fact]
    public void SmpOptionsDoNotChangeASingleThreadedSearch()
    {
        long Run(bool on)
        {
            var engine = new ChessEngine { Threads = 1, UseSmpSharedCorrection = on, UseSmpOddNmp = on };
            long total = 0;
            foreach (string fen in Fens)
                total += engine.FindBestMove(new Board(fen), SearchLimits.Depth(8)).NodesSearched;
            return total;
        }
        Assert.Equal(Run(false), Run(true));
    }

    // With the options on and helpers running, the search must still finish and
    // return a legal move (the tables are written from several threads).
    [Fact]
    public void SmpOptionsOnWithHelpersReturnALegalMove()
    {
        var engine = new ChessEngine { Threads = 3, UseSmpSharedCorrection = true, UseSmpOddNmp = true };
        var board = new Board(Fens[1]);
        var result = engine.FindBestMove(board, SearchLimits.Depth(9));
        Assert.Contains(result.BestMove, MoveGenerator.GenerateLegalMoves(board));
    }
}
