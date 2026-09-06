using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Exactness contract for MoveGenerator.HasEvidentLegalMove: whenever it says
// true, HasLegalMove must say true as well. It is allowed to say false on a
// position that does have legal moves (the caller falls back to the full
// generator then), so the test also reports how often it settles the question,
// which is the whole reason the probe exists.
public class EvidentLegalMoveTests
{
    [Fact]
    public void EvidentTrue_ImpliesLegalMoveExists_OnRandomGamePaths()
    {
        var rng = new Random(20260906);
        var scratch = new MoveList();
        int asked = 0, settled = 0;

        for (int game = 0; game < 200; game++)
        {
            var board = new Board(Board.StartFen);
            for (int plyCount = 0; plyCount < 160; plyCount++)
            {
                var legal = MoveGenerator.GenerateLegalMoves(board);
                if (legal.Count == 0)
                    break;

                if (!board.IsInCheck())
                {
                    asked++;
                    bool evident = MoveGenerator.HasEvidentLegalMove(board);
                    if (evident)
                    {
                        settled++;
                        Assert.True(MoveGenerator.HasLegalMove(board, scratch),
                            $"Probe claimed a legal move on a stalemate: {Fen.Save(board)}");
                    }
                }

                board.MakeMove(legal[rng.Next(legal.Count)]);
            }
        }

        // Random play reaches every phase, including bare endings; the probe
        // has to settle the overwhelming majority or it is not worth its call.
        Assert.True(settled > asked * 95 / 100,
            $"Probe settled only {settled} of {asked} positions");
    }

    [Theory]
    // Classic stalemates: the probe must not invent a move.
    [InlineData("7k/5Q2/6K1/8/8/8/8/8 b - - 0 1")]
    [InlineData("k7/2Q5/8/8/8/8/8/K7 b - - 0 1")]
    // Stalemate with a pinned minor: the only mobile piece cannot move.
    [InlineData("k7/1n1N4/K7/3B4/8/8/8/8 b - - 0 1")]
    [InlineData("kb5R/8/1K6/8/8/8/8/8 b - - 0 1")]
    // Stalemate with a blocked pawn.
    [InlineData("k7/P7/1K6/8/8/8/8/8 b - - 0 1")]
    public void Stalemates_AreNeverClaimedAsMobile(string fen)
    {
        var board = new Board(fen);
        Assert.False(board.IsInCheck());
        Assert.False(MoveGenerator.HasLegalMove(board));
        Assert.False(MoveGenerator.HasEvidentLegalMove(board));
    }

    [Theory]
    // Only a pinned slider can move, along its pin line: the probe may not
    // prove it, but the fallback must, and the two must never disagree.
    [InlineData("k7/8/8/8/8/8/1r6/KR5r w - - 0 1")]
    [InlineData("k7/r2N4/1K6/8/8/8/8/R7 b - - 0 1")]
    // An en passant right on the board, alongside ordinary moves.
    [InlineData("4k3/8/8/2pP4/8/8/8/4K3 w - c6 0 2")]
    public void PositionsWithMoves_NeverContradictTheFullGenerator(string fen)
    {
        var board = new Board(fen);
        Assert.False(board.IsInCheck());
        Assert.True(MoveGenerator.HasLegalMove(board));
        if (MoveGenerator.HasEvidentLegalMove(board))
            Assert.True(MoveGenerator.HasLegalMove(board));
    }
}
