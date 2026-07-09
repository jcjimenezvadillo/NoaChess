using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Heuristics;

namespace NoaChess.Engine.Tests;

// Tests of static exchange evaluation and the v1.0 evaluation terms.
public class SeeAndEvaluationTests
{
    private static Move FindMove(Board board, string uci) =>
        MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci);

    [Fact]
    public void See_QueenTakesDefendedPawn_LosesMaterial()
    {
        // Qxd5 wins a pawn (100) but loses the queen to ...exd5 (900).
        var board = new Board("4k3/8/4p3/3p4/8/8/8/3QK3 w - - 0 1");
        int see = StaticExchangeEvaluator.Evaluate(board, FindMove(board, "d1d5"));
        Assert.Equal(-800, see);
    }

    [Fact]
    public void See_RookTakesUndefendedPawn_WinsMaterial()
    {
        var board = new Board("4k3/8/8/3p4/8/8/8/3RK3 w - - 0 1");
        int see = StaticExchangeEvaluator.Evaluate(board, FindMove(board, "d1d5"));
        Assert.Equal(100, see);
    }

    [Fact]
    public void See_RecaptureSequence_CountsXrays()
    {
        // PxP, and behind the capturing pawn a rook battery continues the
        // sequence: e4xd5 exd5 Rxd5 Rxd5 Rxd5. White wins a pawn overall.
        var board = new Board("3rk3/8/4p3/3p4/4P3/8/8/3RK3 w - - 0 1");
        int see = StaticExchangeEvaluator.Evaluate(board, FindMove(board, "e4d5"));
        Assert.True(see >= 0, $"Expected a non-losing exchange, SEE = {see}");
    }

    [Fact]
    public void PawnStructure_PassedPawnBeatsBlockedPawn()
    {
        var evaluator = new ClassicalEvaluator();

        // Same material; white pawn on a7 is passed and about to promote,
        // in the other board it sits at home facing an enemy pawn on a7.
        var passer = new Board("4k3/P7/8/8/8/8/8/4K3 w - - 0 1");
        var blocked = new Board("4k3/p7/8/8/8/8/P7/4K3 w - - 0 1");

        Assert.True(evaluator.Evaluate(passer) > evaluator.Evaluate(blocked),
            "A far-advanced passed pawn must evaluate higher than a blocked home pawn");
    }

    [Fact]
    public void PawnStructure_PenalizesDoubledIsolatedPawns()
    {
        // Tested on the structure evaluator in isolation (the full evaluation
        // also includes PSTs, which reward advanced central pawns and would
        // muddy the assertion). White: doubled + isolated d4/d5. Black:
        // healthy connected e7/f7 (f7 is technically passed: small bonus).
        var structure = new PawnStructureEvaluator();
        var board = new Board("4k3/4pp2/8/3P4/3P4/8/8/4K3 w - - 0 1");

        // The tapered structure score is White-relative; both phases should be
        // negative for the doubled+isolated side.
        Score score = structure.Evaluate(board);
        Assert.True(score.Mg < 0 && score.Eg < 0,
            "Doubled+isolated pawns vs connected pawns must score negatively");
    }

    [Fact]
    public void Evaluation_StartingPositionIsBalanced()
    {
        // A symmetric position must evaluate to exactly 0: any non-zero result
        // reveals a colour asymmetry in the tapered tables or terms.
        var evaluator = new ClassicalEvaluator();
        Assert.Equal(0, evaluator.Evaluate(new Board(Board.StartFen)));
    }

    [Theory]
    [InlineData("r3k2r/pp1n1ppp/2p1pn2/q7/1b1P4/2N1PN2/PPQ1BPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r2q1rk1/1b1nbppp/p2ppn2/1p6/3NP3/1BN1B3/PPP2PPP/R2Q1RK1 b - - 0 1")]
    public void Evaluation_IsColorSymmetric(string fen)
    {
        // Vertically mirroring the board and swapping colours (and the side to
        // move) yields the same position seen by the other player, so the
        // side-to-move-relative eval must be identical. This catches any colour
        // sign bug in the per-colour terms (king safety, mobility, rook files).
        var evaluator = new ClassicalEvaluator();
        int direct = evaluator.Evaluate(new Board(fen));
        int mirrored = evaluator.Evaluate(new Board(MirrorFen(fen)));
        Assert.Equal(direct, mirrored);
    }

    // Flips a FEN top-to-bottom and swaps piece colours; castling/en passant
    // are dropped (irrelevant to the evaluation symmetry being checked).
    private static string MirrorFen(string fen)
    {
        string[] parts = fen.Split(' ');
        string[] ranks = parts[0].Split('/');
        Array.Reverse(ranks);
        var swapped = ranks.Select(rank => new string(rank.Select(SwapCase).ToArray()));
        string board = string.Join('/', swapped);
        string sideToMove = parts[1] == "w" ? "b" : "w";
        return $"{board} {sideToMove} - - 0 1";

        static char SwapCase(char c) =>
            char.IsUpper(c) ? char.ToLower(c) : char.IsLower(c) ? char.ToUpper(c) : c;
    }

    [Fact]
    public void Evaluation_IsColorSymmetric_RandomPlayoutFuzz()
    {
        // Plays random games and asserts, at EVERY position reached, that the
        // colour-mirrored position evaluates identically. Two hand-picked FENs
        // cannot cover all term interactions (king safety, mobility, mop-up,
        // passed pawns...); a few thousand organic positions can. Fixed seed
        // keeps the test deterministic.
        var evaluator = new ClassicalEvaluator();
        var mirrorEvaluator = new ClassicalEvaluator();
        var rng = new Random(12345);

        for (int game = 0; game < 40; game++)
        {
            var board = new Board(Board.StartFen);
            for (int plyCount = 0; plyCount < 160; plyCount++)
            {
                var legal = MoveGenerator.GenerateLegalMoves(board);
                if (legal.Count == 0)
                    break;
                board.MakeMove(legal[rng.Next(legal.Count)]);

                int direct = evaluator.Evaluate(board);
                int mirrored = mirrorEvaluator.Evaluate(new Board(MirrorBoardFen(board)));
                Assert.True(direct == mirrored,
                    $"Asymmetric eval ({direct} vs {mirrored}) at game {game} ply {plyCount}");
            }
        }
    }

    // Builds the FEN of the colour-mirrored position: every piece moves to its
    // vertically flipped square with the opposite colour, and the side to move
    // is swapped. Castling/en passant are dropped (the evaluator does not read
    // them).
    private static string MirrorBoardFen(Board board)
    {
        var sb = new System.Text.StringBuilder();
        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                // Piece that lands on (rank, file) comes from the flipped rank.
                int source = (7 - rank) * 8 + file;
                PieceType type = board.PieceTypeAt(source);
                if (type == PieceType.None)
                {
                    empty++;
                    continue;
                }
                if (empty > 0) { sb.Append(empty); empty = 0; }
                bool wasWhite = (board.Occupancy(Color.White) & Bitboard.SquareBB(source)) != 0;
                char c = "pnbrqk"[(int)type];
                sb.Append(wasWhite ? c : char.ToUpper(c)); // Swap colours.
            }
            if (empty > 0) sb.Append(empty);
            if (rank > 0) sb.Append('/');
        }
        string stm = board.SideToMove == Color.White ? "b" : "w";
        return $"{sb} {stm} - - 0 1";
    }

    [Fact]
    public void Evaluation_ExposedEnemyKingScoresBetterForUs()
    {
        // Both sides have rooks and a queen (so the middlegame king-safety term
        // is live) and three pawns each. In 'exposed' Black's g-pawn has left
        // the g-file, opening it right next to the black king. Same material,
        // so the higher score for White must come from the king being unsafe.
        var evaluator = new ClassicalEvaluator();
        var shielded = new Board("1r1q1rk1/5ppp/8/8/8/8/5PPP/1R1Q1RK1 w - - 0 1");
        var exposed = new Board("1r1q1rk1/5p1p/5p2/8/8/8/5PPP/1R1Q1RK1 w - - 0 1");
        Assert.True(evaluator.Evaluate(exposed) > evaluator.Evaluate(shielded),
            "An exposed enemy king (open file beside it) must score better for us");
    }

    [Fact]
    public void Evaluation_BishopPairIsRewarded()
    {
        // White has two bishops; the comparison board swaps one for a knight.
        // Both the small material edge and the pair bonus favour the two
        // bishops — a directional sanity check that the pair bonus is applied.
        var evaluator = new ClassicalEvaluator();
        var withPair = new Board("4k3/8/8/8/8/8/8/2B1KB2 w - - 0 1");
        var withoutPair = new Board("4k3/8/8/8/8/8/8/2B1KN2 w - - 0 1");
        Assert.True(evaluator.Evaluate(withPair) > evaluator.Evaluate(withoutPair),
            "Two bishops must score higher than bishop + knight of equal material");
    }
}
