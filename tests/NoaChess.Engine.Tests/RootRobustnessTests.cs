using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// Every legal position with a legal move must produce one. The engine answers
// "bestmove 0000" whenever the search hands back Move.None, and a GUI reads
// that as "no move at all" - in a real game, a loss. The positions here are
// the awkward ones: dead material, the fifty-move edge, forced replies,
// positions whose only continuations repeat, and roots already in check.
public class RootRobustnessTests(ITestOutputHelper output)
{
    [Theory]
    // Dead positions: no mate is possible for either side.
    [InlineData("8/8/8/4k3/8/8/8/4K3 w - - 0 1")]
    [InlineData("8/8/8/4k3/8/8/8/3NK3 w - - 0 1")]
    [InlineData("8/8/8/4k3/8/8/8/3BK3 b - - 0 1")]
    // One ply from the fifty-move draw, with a pawn move that resets it.
    [InlineData("8/8/8/4k3/8/8/4P3/4K3 w - - 99 60")]
    // The fifty-move clock already at 100 with moves still on the board.
    [InlineData("8/8/8/4k3/8/8/4P3/4K3 w - - 100 60")]
    // In check with a single escape.
    [InlineData("7k/8/8/8/8/8/6q1/7K w - - 0 1")]
    // In check from a knight: only the king may answer.
    [InlineData("4k3/8/8/8/8/5n2/8/4K3 w - - 4 30")]
    // A bare king against a queen, on the move.
    [InlineData("8/8/8/4k3/8/8/8/3QK3 b - - 0 1")]
    // Every legal move walks into a repetition of the current position.
    [InlineData("7k/8/8/8/8/8/8/R6K w - - 20 60")]
    // A crowded root: the full opening array, and a middlegame with pins.
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8")]
    public void EveryLegalRootProducesALegalMove(string fen)
    {
        var board = new Board(fen);
        List<Move> legal = MoveGenerator.GenerateLegalMoves(board);
        Assert.NotEmpty(legal); // The position itself must offer a move.

        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(8));
        output.WriteLine($"{fen} -> {result.BestMove} score {result.Score} nodes {result.NodesSearched}");

        Assert.NotEqual(Move.None, result.BestMove);
        Assert.Contains(legal, m => m == result.BestMove);
    }

    // The two roots that legitimately have no move keep answering with none,
    // and with the score that says which of the two they are.
    [Theory]
    [InlineData("R5k1/5ppp/8/8/8/8/8/6K1 b - - 0 1", -AlphaBetaSearch.MateScore)] // back rank mate
    [InlineData("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1", 0)]                            // stalemate
    [InlineData("7k/5Q2/8/8/8/8/8/6K1 b - - 0 1", 0)]                            // stalemate, king away
    public void TerminalRootsAnswerWithTheGameResult(string fen, int expected)
    {
        var board = new Board(fen);
        Assert.Empty(MoveGenerator.GenerateLegalMoves(board));

        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(6));

        Assert.Equal(Move.None, result.BestMove);
        Assert.Equal(expected, result.Score);
    }
}
