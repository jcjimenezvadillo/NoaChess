using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// The Lazy SMP vote picks the move the engine actually plays, while the "info"
// lines the GUI sees come from the main worker alone. When the two disagree the
// engine plays a move its own printed variation never mentions, which is exactly
// what happened on 2026-08-09: two mates in one in four minutes, every info line
// reporting the correct move.
//
// VoteBestResult is private, so these go through reflection rather than widening
// its visibility for a test.
public class VoteBestResultTests
{
    // An instance method since SmpVoteAll (the gate depends on engine state);
    // a fresh engine leaves the option at its default, so these tests keep
    // exercising the strict gate they were written against.
    private static SearchResult Vote(params SearchResult[] results)
    {
        MethodInfo method = typeof(ChessEngine).GetMethod(
            "VoteBestResult", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (SearchResult)method.Invoke(new ChessEngine(), [results])!;
    }

    // The squares are arbitrary: the vote only ever compares moves for equality.
    private static Move MoveOf(int from, int to) => new(from, to, MoveFlag.Quiet);

    [Fact]
    public void ShallowHelperCannotOverruleDeeperMainWorker()
    {
        // The real numbers from lichess.org/yuu7lvp6, move 18. The main worker
        // had Bf6 at depth 12 with -1613; a starved helper had Nxe3 at depth 1
        // with -971, a score that only looks better because depth 1 has not seen
        // Qxg7#. Weighted purely by score the helper wins 656 votes to 14.
        Move bishopF6 = MoveOf(52, 45);
        Move knightTakesE3 = MoveOf(10, 20);

        SearchResult chosen = Vote(
            new SearchResult(bishopF6, -1613, 500_000, 12),
            new SearchResult(knightTakesE3, -971, 78, 1));

        Assert.Equal(bishopF6, chosen.BestMove);
    }

    [Fact]
    public void DeeperHelperStillWinsTheVote()
    {
        // The fix must not disable the vote. A helper that searched DEEPER than
        // the main worker is exactly what Lazy SMP is for, and its verdict
        // should still carry.
        Move mainMove = MoveOf(12, 28);
        Move deeperMove = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(mainMove, -300, 100_000, 10),
            new SearchResult(deeperMove, -50, 400_000, 14));

        Assert.Equal(deeperMove, chosen.BestMove);
    }

    [Fact]
    public void EqualDepthHelperWithBetterScoreWins()
    {
        Move mainMove = MoveOf(12, 28);
        Move peerMove = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(mainMove, -300, 100_000, 12),
            new SearchResult(peerMove, -40, 120_000, 12));

        Assert.Equal(peerMove, chosen.BestMove);
    }

    [Fact]
    public void HelperThatNeverReportedIsIgnored()
    {
        // A worker cancelled before it wrote anything leaves an empty result.
        Move mainMove = MoveOf(12, 28);

        SearchResult chosen = Vote(
            new SearchResult(mainMove, -300, 100_000, 9),
            new SearchResult(Move.None, 0, 0, 0));

        Assert.Equal(mainMove, chosen.BestMove);
    }

    [Fact]
    public void ShortestMateIsPreferredAmongDecisiveLines()
    {
        // Pre-existing behaviour that the depth filter must not break.
        Move slowMate = MoveOf(12, 28);
        Move fastMate = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(slowMate, AlphaBetaSearch.MateScore - 20, 100_000, 12),
            new SearchResult(fastMate, AlphaBetaSearch.MateScore - 4, 120_000, 12));

        Assert.Equal(fastMate, chosen.BestMove);
    }

    [Fact]
    public void HelperThatBelievesItIsMatedDoesNotWinTheVote()
    {
        // "Decisive" was tested with Math.Abs, so a worker reporting a forced
        // LOSS satisfied it - and that test short-circuits the vote outright,
        // ahead of any score comparison. One worker announcing its own defeat
        // took the move away from every other worker on the board.
        Move sane = MoveOf(12, 28);
        Move losing = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(sane, -120, 400_000, 14),
            new SearchResult(losing, -AlphaBetaSearch.MateScore + 6, 90_000, 14));

        Assert.Equal(sane, chosen.BestMove);
    }

    [Fact]
    public void AmongLostLinesTheFastestMateIsNotPreferred()
    {
        // The follow-on half of the same defect: once a lost result held the
        // lead, the decisive branch kept whichever had the largest |score|,
        // which among losses is the SHORTEST mate. Given only bad news the
        // vote actively chose to be mated sooner. The main worker's result
        // stands instead.
        Move matedIn20 = MoveOf(12, 28);
        Move matedIn2 = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(matedIn20, -AlphaBetaSearch.MateScore + 40, 100_000, 12),
            new SearchResult(matedIn2, -AlphaBetaSearch.MateScore + 4, 120_000, 12));

        Assert.Equal(matedIn20, chosen.BestMove);
    }

    [Fact]
    public void AWinningWorkerStillOverrulesALostOne()
    {
        // The sign fix must not cost the vote its point: a worker that found a
        // forced win has to be able to take the move from a worker that only
        // sees a loss, whatever the ordinary weighting says.
        Move lost = MoveOf(12, 28);
        Move mateFound = MoveOf(11, 27);

        SearchResult chosen = Vote(
            new SearchResult(lost, -AlphaBetaSearch.MateScore + 10, 400_000, 14),
            new SearchResult(mateFound, AlphaBetaSearch.MateScore - 8, 90_000, 14));

        Assert.Equal(mateFound, chosen.BestMove);
    }
}
