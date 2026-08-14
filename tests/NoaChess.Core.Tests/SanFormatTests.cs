using NoaChess.Core;

namespace NoaChess.Core.Tests;

// San.Format tests: the writer must produce exactly what the reader accepts.
// Disambiguation is where SAN writers usually go wrong, so every shape of it
// gets its own case (file, rank, and both).
public class SanFormatTests
{
    // Formats the UCI move 'uci' in the position 'fen'.
    private static string Format(string fen, string uci)
    {
        var board = new Board(fen);
        Move move = MoveGenerator.GenerateLegalMoves(board)
            .Single(m => m.ToString() == uci);
        return San.Format(board, move);
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "e2e4", "e4")]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", "g1f3", "Nf3")]
    // Pawn capture: named by the file it leaves, not by the piece letter.
    [InlineData("rnbqkbnr/ppp1pppp/8/3p4/4P3/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 2", "e4d5", "exd5")]
    // En passant is written like any other pawn capture.
    [InlineData("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3", "e5f6", "exf6")]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1", "b8c6", "Nc6")]
    public void PlainMoves(string fen, string uci, string expected)
        => Assert.Equal(expected, Format(fen, uci));

    [Theory]
    // Both rooks reach d1 and they differ in file, so the file is enough.
    [InlineData("4k3/8/8/8/4K3/8/8/R6R w - - 0 1", "a1d1", "Rad1")]
    // Same file, different rank: the rank is what separates them.
    [InlineData("R7/8/8/4k3/8/8/8/R3K3 w - - 0 1", "a1a4", "R1a4")]
    // Three queens reach d4: one shares the file with the mover and another
    // shares its rank, so neither hint alone is enough and SAN needs the
    // full origin square.
    [InlineData("8/8/7k/8/Q7/8/8/Q2Q3K w - - 0 1", "a1d4", "Qa1d4")]
    // Both knights reach g3, but the e2 one is pinned against its king by the
    // rook on e8 and cannot legally move: a piece that is not allowed to go
    // there does not make the notation ambiguous.
    [InlineData("k3r3/8/8/8/8/8/4N3/4K2N w - - 0 1", "h1g3", "Ng3")]
    public void Disambiguation(string fen, string uci, string expected)
        => Assert.Equal(expected, Format(fen, uci));

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1", "e1g1", "O-O")]
    [InlineData("4k3/8/8/8/8/8/8/R3K2R w KQ - 0 1", "e1c1", "O-O-O")]
    // Castling can itself give check: the rook lands on d8 with the white king
    // sitting on the d file.
    [InlineData("r3k2r/8/8/8/8/8/8/3K4 b kq - 0 1", "e8c8", "O-O-O+")]
    public void Castling(string fen, string uci, string expected)
        => Assert.Equal(expected, Format(fen, uci));

    [Theory]
    [InlineData("1n6/P7/7k/8/8/8/8/4K3 w - - 0 1", "a7a8q", "a8=Q")]
    [InlineData("1n6/P7/7k/8/8/8/8/4K3 w - - 0 1", "a7a8n", "a8=N")]
    // Capture-promotion: origin file, x, destination and the new piece.
    [InlineData("1n6/P7/7k/8/8/8/8/4K3 w - - 0 1", "a7b8q", "axb8=Q")]
    public void Promotions(string fen, string uci, string expected)
        => Assert.Equal(expected, Format(fen, uci));

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/K5R1 w - - 0 1", "g1e1", "Re1+")]
    // Back-rank mate gets '#', and the position it is measured on is the one
    // AFTER the move, so the board has to be advanced and restored.
    [InlineData("6k1/5ppp/8/8/8/8/8/R3K3 w Q - 0 1", "a1a8", "Ra8#")]
    public void CheckAndMateMarks(string fen, string uci, string expected)
        => Assert.Equal(expected, Format(fen, uci));

    [Fact]
    public void FormatIsTheExactInverseOfTryParse()
    {
        // Every legal move of a busy middlegame position must survive the round
        // trip. This is the property that matters: whatever the writer emits,
        // the reader has to resolve back to the very same move.
        var board = new Board("r1bqk2r/pp1n1ppp/2pbpn2/3p4/2PP4/2N1PN2/PPQ1BPPP/R1B1K2R w KQkq - 0 8");
        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
        {
            string san = San.Format(board, move);
            Assert.True(San.TryParse(board, san, out Move parsed), $"'{san}' did not parse back");
            Assert.Equal(move, parsed);
        }
    }

    [Fact]
    public void FormatLeavesTheBoardUntouched()
    {
        // Format plays the move to look for check and then takes it back. If
        // the restore were not exact, a move list would silently corrupt the
        // game it is describing.
        const string fen = "r1bqk2r/pp1n1ppp/2pbpn2/3p4/2PP4/2N1PN2/PPQ1BPPP/R1B1K2R w KQkq - 0 8";
        var board = new Board(fen);
        ulong key = board.ZobristKey;

        foreach (Move move in MoveGenerator.GenerateLegalMoves(board))
            San.Format(board, move);

        Assert.Equal(fen, Fen.Save(board));
        Assert.Equal(key, board.ZobristKey);
    }

    [Fact]
    public void NoMoveIsWrittenAsADash()
        => Assert.Equal("--", San.Format(new Board(), Move.None));
}
