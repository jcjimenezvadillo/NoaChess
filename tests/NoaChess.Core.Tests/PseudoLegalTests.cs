using NoaChess.Core;

namespace NoaChess.Core.Tests;

// Exactness contract for MoveGenerator.IsPseudoLegal: for ANY move, the
// predicate must return true if and only if the pseudo-legal generator of the
// position would emit that move. The staged move picker relies on it to vet
// transposition-table moves before making them — a false positive there would
// hand the board a corrupting garbage move (e.g. moving from an empty square),
// a false negative would silently drop the best move.
public class PseudoLegalTests
{
    [Fact]
    public void IsPseudoLegal_MatchesGeneratorOnRandomGamePaths()
    {
        var rng = new Random(20260710);
        var scratch = new MoveList();

        for (int game = 0; game < 60; game++)
        {
            var board = new Board(Board.StartFen);
            Move previousPositionMove = Move.None;

            for (int plyCount = 0; plyCount < 120; plyCount++)
            {
                var legal = MoveGenerator.GenerateLegalMoves(board);
                if (legal.Count == 0)
                    break;

                // 1) No false negatives: every generated pseudo-legal move
                //    must pass the predicate.
                MoveGenerator.GeneratePseudoLegalMoves(board, scratch);
                for (int i = 0; i < scratch.Count; i++)
                {
                    Assert.True(MoveGenerator.IsPseudoLegal(board, scratch[i]),
                        $"Generated move {scratch[i]} rejected at game {game} ply {plyCount}: {Fen.Save(board)}");
                }

                // 2) The staged appenders (captures + quiets) must produce the
                //    exact same SET as the one-shot generator.
                var staged = new MoveList();
                MoveGenerator.AppendCaptureMoves(board, staged);
                MoveGenerator.AppendQuietMoves(board, staged);
                Assert.Equal(scratch.Count, staged.Count);
                for (int i = 0; i < staged.Count; i++)
                    Assert.True(scratch.Contains(staged[i]),
                        $"Staged move {staged[i]} not in one-shot set at {Fen.Save(board)}");

                // 3) No false positives: a move that passes the predicate must
                //    be in the generated set. Probed with a stale TT-style move
                //    (a move from an EARLIER position — the exact collision
                //    scenario) and with random 16-bit encodings.
                if (previousPositionMove != Move.None)
                    Assert.Equal(scratch.Contains(previousPositionMove),
                                 MoveGenerator.IsPseudoLegal(board, previousPositionMove));

                for (int probe = 0; probe < 200; probe++)
                {
                    var m = new Move(rng.Next(64), rng.Next(64), (MoveFlag)rng.Next(16));
                    Assert.Equal(scratch.Contains(m), MoveGenerator.IsPseudoLegal(board, m));
                }

                Move chosen = legal[rng.Next(legal.Count)];
                previousPositionMove = chosen;
                board.MakeMove(chosen);
            }
        }
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w KQ - 0 1")]
    [InlineData("4k3/8/8/8/8/8/8/3K3R w K - 0 1")]
    public void StaleCastlingRights_DoNotCreatePiecesOrMoves(string fen)
    {
        var board = new Board(fen);
        int piecesBefore = Bitboard.PopCount(board.AllOccupancy);
        var kingSide = new Move(Squares.Parse("e1"), Squares.Parse("g1"), MoveFlag.KingCastle);
        var queenSide = new Move(Squares.Parse("e1"), Squares.Parse("c1"), MoveFlag.QueenCastle);

        var pseudo = MoveGenerator.GeneratePseudoLegalMoves(board);

        Assert.DoesNotContain(kingSide, pseudo);
        Assert.DoesNotContain(queenSide, pseudo);
        Assert.False(MoveGenerator.IsPseudoLegal(board, kingSide));
        Assert.False(MoveGenerator.IsPseudoLegal(board, queenSide));
        Assert.Equal(piecesBefore, Bitboard.PopCount(board.AllOccupancy));
    }
}
