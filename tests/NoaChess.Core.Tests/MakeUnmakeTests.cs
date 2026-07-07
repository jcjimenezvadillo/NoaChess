using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Verifies that MakeMove + UnmakeMove restores EXACTLY the previous state,
// including the Zobrist hash. This is the contract the whole engine search
// depends on (it makes and unmakes millions of moves on the same board).
public class MakeUnmakeTests
{
    [Theory]
    [InlineData(Board.StartFen)]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2")]
    [InlineData("r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1")]
    public void MakeUnmake_RestoresExactState(string fen)
    {
        var board = new Board(fen);
        string originalFen = Fen.Save(board);
        ulong originalHash = board.ZobristKey;

        // EVERY legal move of the position is tried, including castles,
        // en passant and promotions depending on the chosen position.
        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
        {
            board.MakeMove(move);
            board.UnmakeMove();

            Assert.Equal(originalFen, Fen.Save(board));
            Assert.Equal(originalHash, board.ZobristKey);
        }
    }

    [Fact]
    public void ZobristHash_IsIncrementallyConsistent()
    {
        // The incrementally maintained hash after several moves must match the
        // hash computed from scratch by loading the resulting FEN.
        var board = new Board();
        foreach (string uci in new[] { "e2e4", "c7c5", "g1f3", "d7d6", "d2d4", "c5d4" })
        {
            Move move = MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci);
            board.MakeMove(move);
        }

        var fresh = new Board(Fen.Save(board));
        Assert.Equal(fresh.ZobristKey, board.ZobristKey);
    }

    [Fact]
    public void Castling_MovesRookToo()
    {
        // White can castle short: after O-O the king is on g1 and the rook on f1.
        var board = new Board("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");
        Move castle = MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == "e1g1");

        board.MakeMove(castle);
        Assert.Equal(PieceType.King, board.PieceTypeAt(Squares.Parse("g1")));
        Assert.Equal(PieceType.Rook, board.PieceTypeAt(Squares.Parse("f1")));
        Assert.True(board.IsEmpty(Squares.Parse("h1")));
        // Castling removes the white rights.
        Assert.False(board.CastlingRights.HasFlag(CastlingRights.WhiteKingSide));
        Assert.False(board.CastlingRights.HasFlag(CastlingRights.WhiteQueenSide));
    }

    [Fact]
    public void EnPassant_RemovesCapturedPawn()
    {
        // White pawn on e5; d7d5 creates en passant on d6; exd6 captures the d5 pawn.
        var board = new Board("rnbqkbnr/pppppppp/8/4P3/8/8/PPPP1PPP/RNBQKBNR b KQkq - 0 2");
        board.MakeMove(MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == "d7d5"));

        Move ep = MoveGenerator.GenerateLegalMoves(board).First(m => m.Flag == MoveFlag.EnPassant);
        Assert.Equal("e5d6", ep.ToString());

        board.MakeMove(ep);
        Assert.True(board.IsEmpty(Squares.Parse("d5"))); // The captured pawn disappears.
        Assert.Equal(PieceType.Pawn, board.PieceTypeAt(Squares.Parse("d6")));
    }
}
