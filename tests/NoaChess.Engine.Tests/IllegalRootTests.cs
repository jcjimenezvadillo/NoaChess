using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// A position where the side NOT to move is in check cannot arise in a game,
// but a GUI can set one up by hand and press go. The engine must answer with a
// legal move rather than throwing, because the UCI layer turns a thrown search
// into "bestmove 0000" and a GUI reads that as "no move".
public class IllegalRootTests(ITestOutputHelper output)
{
    [Theory]
    // Black in check from the queen down the d-file, White to move.
    [InlineData("8/8/8/3k4/8/8/8/3QK3 w - - 0 1")]
    // The same with a rook, and from further away.
    [InlineData("3k4/8/8/8/8/8/8/3RK3 w - - 0 1")]
    // A knight, which no blocking move could ever answer.
    [InlineData("8/8/8/3k4/5N2/8/8/4K3 w - - 0 1")]
    // And the mirror case, with White in check and Black to move.
    [InlineData("4k3/8/8/8/8/8/8/3rK3 b - - 0 1")]
    public void SearchAnswersAPositionWhoseOpponentIsInCheck(string fen)
    {
        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        var board = new Board(fen);

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(4));
        output.WriteLine($"{fen} -> {result.BestMove} score {result.Score}");

        Assert.NotEqual(Move.None, result.BestMove);
        Assert.Contains(MoveGenerator.GenerateLegalMoves(board), m => m == result.BestMove);
    }

    // The answer is the capture itself when one is available, so the score the
    // engine reports matches what it is about to do.
    [Fact]
    public void TheAnswerCapturesTheKingWhenItCan()
    {
        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        var board = new Board("8/8/8/3k4/8/8/8/3QK3 w - - 0 1");

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(4));

        Assert.Equal("d1d5", result.BestMove.ToString());
    }

    // Legal positions must not notice the guard: the same search, same answer.
    [Fact]
    public void LegalPositionsAreUntouched()
    {
        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        var board = new Board("r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8");

        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(8));

        Assert.NotEqual(Move.None, result.BestMove);
        Assert.True(result.NodesSearched > 1000,
            $"the guard swallowed the search: {result.NodesSearched} nodes");
    }
}
