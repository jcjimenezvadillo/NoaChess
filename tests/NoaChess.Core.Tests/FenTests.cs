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

    [Theory]
    [InlineData("8/8/8/8/8/8/8/4K3 w - - 0 1")]             // Missing black king.
    [InlineData("4k3/8/8/8/8/8/8/3K3 w - - 0 1")]           // Short first rank.
    [InlineData("4k3/8/8/8/8/8/8/4K3 x - - 0 1")]           // Invalid side.
    [InlineData("4k3/8/8/8/8/8/8/4K3 w KK - 0 1")]          // Duplicate right.
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - e3 0 1")]          // Wrong EP rank/pawn.
    [InlineData("4k3/8/8/8/8/8/8/P3K3 w - - 0 1")]          // Pawn on rank one.
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - -1 1")]         // Negative clock.
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 0")]          // Fullmove starts at one.
    public void StructurallyInvalidFen_Throws(string fen)
    {
        Assert.Throws<ArgumentException>(() => new Board(fen));
    }

    [Fact]
    public void RejectedFen_DoesNotMutateExistingBoard()
    {
        var board = new Board();
        string before = Fen.Save(board);
        ulong keyBefore = board.ZobristKey;

        Assert.Throws<ArgumentException>(
            () => Fen.Load(board, "4k3/8/8/8/8/8/8/4K3 nope - - 0 1"));

        Assert.Equal(before, Fen.Save(board));
        Assert.Equal(keyBefore, board.ZobristKey);
    }
}
