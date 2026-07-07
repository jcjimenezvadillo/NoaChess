using NoaChess.Core;

namespace NoaChess.Core.Tests;

// FEN parsing and serialization tests: loading and saving back must be identical.
public class FenTests
{
    [Theory]
    [InlineData(Board.StartFen)]
    // "Kiwipete": classic test position, packed with special cases.
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    // Position with en passant available.
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2")]
    // Endgame with few castling rights.
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 5 39")]
    public void RoundTrip_PreservesFen(string fen)
    {
        var board = new Board(fen);
        Assert.Equal(fen, Fen.Save(board));
    }

    [Fact]
    public void StartPosition_HasCorrectState()
    {
        var board = new Board();
        Assert.Equal(Color.White, board.SideToMove);
        Assert.Equal(CastlingRights.All, board.CastlingRights);
        Assert.Equal(Squares.None, board.EnPassantSquare);
        Assert.Equal(PieceType.Rook, board.PieceTypeAt(Squares.Parse("a1")));
        Assert.Equal(PieceType.King, board.PieceTypeAt(Squares.Parse("e8")));
        Assert.Equal(Color.Black, board.ColorAt(Squares.Parse("e8")));
        Assert.Equal(32, Bitboard.PopCount(board.AllOccupancy));
    }

    [Fact]
    public void InvalidFen_Throws()
    {
        Assert.ThrowsAny<Exception>(() => new Board("not a fen"));
    }
}
