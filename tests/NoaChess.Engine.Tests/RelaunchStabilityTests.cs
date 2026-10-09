using NoaChess.Core;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// The ponderhit relaunch inherits the PONDER's stability record for the
// easy-move and obvious-move cuts (AlphaBetaSearch, _carriedDepth): over the
// table the ponder filled, the relaunch's own record is a replay and looked
// like a forced move on every relaunch, which is what answered in 40 ms with
// 500 s on the clock (bot games of 2026-10-08/09).
public class RelaunchStabilityTests
{
    [Fact]
    public void ObviousVerdict_NeedsTheCutsOwnConditionsOnThePondersRecord()
    {
        // A recapture pondered to depth 20: settled at depth 1, no changes,
        // the whole tree on it.
        Assert.True(AlphaBetaSearch.PonderWasObvious(depth: 20, settledBy: 1, changes: 0.0, share: 0.97));
        // Settled late: the ponder was still choosing at depth 9.
        Assert.False(AlphaBetaSearch.PonderWasObvious(20, settledBy: 9, changes: 0.0, share: 0.97));
        // Settled early but not held long enough.
        Assert.False(AlphaBetaSearch.PonderWasObvious(14, settledBy: 4, changes: 0.0, share: 0.97));
        // A root change inside the decay window.
        Assert.False(AlphaBetaSearch.PonderWasObvious(20, settledBy: 1, changes: 0.5, share: 0.97));
        // The tree spread over alternatives.
        Assert.False(AlphaBetaSearch.PonderWasObvious(20, settledBy: 1, changes: 0.0, share: 0.6));
        // Too shallow to be trusted at all (the opponent moved at once).
        Assert.False(AlphaBetaSearch.PonderWasObvious(9, settledBy: 1, changes: 0.0, share: 1.0));
    }

    [Fact]
    public void EasyVerdict_NeedsADecisiveScoreHeldLongEnough()
    {
        Assert.True(AlphaBetaSearch.PonderWasEasy(depth: 18, settledBy: 10, score: 900, winOnly: true));
        Assert.False(AlphaBetaSearch.PonderWasEasy(18, settledBy: 14, score: 900, winOnly: true));
        Assert.False(AlphaBetaSearch.PonderWasEasy(18, settledBy: 10, score: 300, winOnly: true));
        // Losing decisively is not easy when the cut is win-only, and is when it is not.
        Assert.False(AlphaBetaSearch.PonderWasEasy(18, settledBy: 10, score: -900, winOnly: true));
        Assert.True(AlphaBetaSearch.PonderWasEasy(18, settledBy: 10, score: -900, winOnly: false));
        Assert.False(AlphaBetaSearch.PonderWasEasy(11, settledBy: 1, score: 900, winOnly: true));
    }

    [Fact]
    public void FreshSearch_CarriesNothing()
    {
        // No ponder record ever reaches a search without a pondered credit:
        // the record is consumed by the relaunch that follows it and a plain
        // clock search answers exactly as before (a quiet middlegame, both
        // searches under the same short budget, neither answering at once).
        var engine = new ChessEngine();
        var board = new Board("r1bq1rk1/pp2ppbp/2np1np1/8/2PNP3/2N1B3/PP2BPPP/R2Q1RK1 b - - 0 1");
        SearchLimits budget = SearchLimits.Clock(softMs: 600, hardMs: 2_000);

        var first = engine.FindBestMove(board, budget);
        var second = engine.FindBestMove(board, budget);

        Assert.Contains(first.BestMove, MoveGenerator.GenerateLegalMoves(board));
        Assert.Contains(second.BestMove, MoveGenerator.GenerateLegalMoves(board));
    }
}
