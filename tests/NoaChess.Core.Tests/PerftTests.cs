using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Perft tests: they compare the number of reachable positions against public
// reference values (https://www.chessprogramming.org/Perft_Results).
// They are the most reliable validation of the move generator: any bug in
// castling, en passant, promotions or pins makes some count mismatch.
// Depths are limited so the suite runs in seconds.
public class PerftTests
{
    // Kiwipete: designed specifically to stress special cases.
    private const string Kiwipete = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    // Position 3 from the wiki: lots of en passant and pins.
    private const string Position3 = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";

    // Position 4: promotions and discovered checks.
    private const string Position4 = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";

    [Theory]
    [InlineData(Board.StartFen, 1, 20)]
    [InlineData(Board.StartFen, 2, 400)]
    [InlineData(Board.StartFen, 3, 8_902)]
    [InlineData(Board.StartFen, 4, 197_281)]
    [InlineData(Kiwipete, 1, 48)]
    [InlineData(Kiwipete, 2, 2_039)]
    [InlineData(Kiwipete, 3, 97_862)]
    [InlineData(Position3, 1, 14)]
    [InlineData(Position3, 2, 191)]
    [InlineData(Position3, 3, 2_812)]
    [InlineData(Position3, 4, 43_238)]
    [InlineData(Position4, 1, 6)]
    [InlineData(Position4, 2, 264)]
    [InlineData(Position4, 3, 9_467)]
    public void Perft_MatchesReferenceValues(string fen, int depth, long expectedNodes)
    {
        var board = new Board(fen);
        Assert.Equal(expectedNodes, Perft.Count(board, depth));
    }
}
