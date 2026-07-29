using NoaChess.Core;
using Xunit;

namespace NoaChess.Engine.Tests;

// SAN parser: the correctness gate for PGN replay (opening-book seeding). A
// wrong parse silently produces a wrong position, so this replays a full real
// game and checks the final position against the PGN's own CurrentPosition.
public class SanTests
{
    // A real chess.com game (perci1 vs 01Roman, 2019, 2815 Elo). Exercises pawn
    // moves, piece moves with captures, castling on both sides, EN PASSANT
    // (18. exf6), check '+' and mate '#' suffixes. The expected final position
    // is the game's own [CurrentPosition] header.
    private const string SampleGame =
        "d4 d5 c4 dxc4 Nf3 Nf6 e3 e6 Bxc4 a6 a4 c5 O-O Nc6 Nc3 Be7 Qe2 Qc7 b3 Na5 " +
        "dxc5 Bxc5 e4 O-O e5 Nd7 Bd3 Nxb3 Bxh7+ Kxh7 Ng5+ Kg6 Qe4+ f5 exf6+ Kxf6 Qxe6#";

    private const string SampleFinalPosition =
        "r1b2r2/1pqn2p1/p3Qk2/2b3N1/P7/1nN5/5PPP/R1B2RK1 b - -";

    private static Board Replay(string startFen, string sanMoves)
    {
        Board board = startFen.Length == 0 ? new Board() : new Board(startFen);
        foreach (string san in sanMoves.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.True(San.TryParse(board, san, out Move move), $"failed to parse SAN '{san}'");
            board.MakeMove(move);
        }
        return board;
    }

    private static string PositionFields(Board board) =>
        string.Join(' ', Fen.Save(board).Split(' ')[..4]); // placement + stm + castling + ep

    [Fact]
    public void ReplaysRealGameToExactFinalPosition()
    {
        Board board = Replay("", SampleGame);
        Assert.Equal(SampleFinalPosition, PositionFields(board));
    }

    [Fact]
    public void ParsesPromotionWithEquals()
    {
        Board board = new("6k1/4P3/8/8/8/8/8/4K3 w - - 0 1");
        Assert.True(San.TryParse(board, "e8=Q+", out Move move));
        Assert.True(move.IsPromotion);
        Assert.Equal(PieceType.Queen, move.PromotionPiece);
        Assert.Equal("e7e8q", move.ToString());
    }

    [Fact]
    public void ParsesUnderpromotion()
    {
        Board board = new("6k1/4P3/8/8/8/8/8/4K3 w - - 0 1");
        Assert.True(San.TryParse(board, "e8=N", out Move move));
        Assert.Equal(PieceType.Knight, move.PromotionPiece);
    }

    [Fact]
    public void ResolvesFileDisambiguation()
    {
        // Knights on c3 and g1 both reach e2; "Nge2" must pick the g1 knight.
        Board board = new("4k3/8/8/8/8/2N5/8/4K1N1 w - - 0 1");
        Assert.True(San.TryParse(board, "Nge2", out Move move));
        Assert.Equal(Squares.FromFileRank(6, 0), move.From); // g1
        Assert.True(San.TryParse(board, "Nce2", out Move other));
        Assert.Equal(Squares.FromFileRank(2, 2), other.From); // c3
    }

    [Fact]
    public void RejectsIllegalAndAmbiguousTokens()
    {
        Board board = new();
        Assert.False(San.TryParse(board, "e5", out _));   // no pawn can reach e5 from startpos
        Assert.False(San.TryParse(board, "Zz9", out _));  // garbage
        Assert.False(San.TryParse(board, "", out _));     // empty
    }
}
