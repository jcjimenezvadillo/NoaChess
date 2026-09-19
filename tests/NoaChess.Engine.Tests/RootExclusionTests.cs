using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// FindBestMove's 'excludedRootMoves' parameter (2026-09-18), added for
// DataGen's self-play root diversity: play a genuine second-best move
// sometimes instead of always the single deterministic best one.
public class RootExclusionTests
{
    private readonly ChessEngine _engine = new();

    [Fact]
    public void NoExclusion_MatchesTheOldSignatureExactly()
    {
        var board = new Board("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        SearchResult withoutParam = _engine.FindBestMove(board, SearchLimits.Depth(6));
        SearchResult withNullExclusion = _engine.FindBestMove(board, SearchLimits.Depth(6),
            excludedRootMoves: null);

        Assert.Equal(withoutParam.BestMove, withNullExclusion.BestMove);
        Assert.Equal(withoutParam.Score, withNullExclusion.Score);
        Assert.Equal(withoutParam.NodesSearched, withNullExclusion.NodesSearched);
    }

    [Fact]
    public void ExcludingTheBestMove_ReturnsADifferentLegalMove()
    {
        // Ra8# is the only mate here; excluding it must still return some
        // other real, legal move rather than repeating it or crashing.
        var board = new Board("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        SearchResult best = _engine.FindBestMove(board, SearchLimits.Depth(6));
        Assert.Equal("a1a8", best.BestMove.ToString());

        var excluded = new MoveList();
        excluded.Add(best.BestMove);
        SearchResult second = _engine.FindBestMove(board, SearchLimits.Depth(6),
            excludedRootMoves: excluded);

        Assert.NotEqual(Move.None, second.BestMove);
        Assert.NotEqual(best.BestMove, second.BestMove);
        Assert.Contains(second.BestMove, MoveGenerator.GenerateLegalMoves(board));
        // The excluded move truly is best: whatever is left cannot mate in one.
        Assert.True(second.Score < AlphaBetaSearch.MateScore - 2);
    }

    [Fact]
    public void ExcludingEveryLegalMove_ReturnsNoMoveInsteadOfCrashing()
    {
        // A position with exactly one legal move (forced recapture aside,
        // any position with a single reply works); excluding that one move
        // leaves nothing to search.
        var board = new Board("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        var all = MoveGenerator.GenerateLegalMoves(board);
        var excluded = new MoveList();
        for (int i = 0; i < all.Count; i++)
            excluded.Add(all[i]);

        SearchResult result = _engine.FindBestMove(board, SearchLimits.Depth(6),
            excludedRootMoves: excluded);

        Assert.Equal(Move.None, result.BestMove);
    }
}
