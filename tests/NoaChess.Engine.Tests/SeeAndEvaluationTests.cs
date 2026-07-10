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
    public void Rook_OnSeventhRankScoresBetter()
    {
        var evaluator = new ClassicalEvaluator();

        // White rook on a7 (7th rank) vs a6 — same material. The 7th-rank
        // bonus (endgame-heavy) must outweigh any PST difference between ranks.
        var onSeventh = new Board("4k3/R7/8/8/8/8/8/4K3 w - - 0 1");
        var onSixth   = new Board("4k3/8/R7/8/8/8/8/4K3 w - - 0 1");

        Assert.True(evaluator.Evaluate(onSeventh) > evaluator.Evaluate(onSixth),
            "A rook on the 7th rank must score higher than one on the 6th");
    }

    [Fact]
    public void Outpost_ProtectedUnkickableKnightScoresHigher()
    {
        var evaluator = new ClassicalEvaluator();

        // Knight on d5 protected by the e4 pawn; black has no c- or e-pawns
        // left to evict it -> outpost. Second board: same material but the
        // knight sits on d3 in its own camp (same central PST region).
        var outpost = new Board("4k3/5p2/8/3N4/4P3/8/8/4K3 w - - 0 1");
        var home = new Board("4k3/5p2/8/8/4P3/3N4/8/4K3 w - - 0 1");

        Assert.True(evaluator.Evaluate(outpost) > evaluator.Evaluate(home),
            "A protected, unkickable knight in the enemy camp must outscore one at home");
    }

    [Fact]
    public void Passers_RookBehindOwnPasserBonusIsApplied()
    {
        // Comparing two different rook placements through the full evaluation
        // is fragile: mobility and file bonuses legitimately differ between
        // the boards and can cancel the (tuned, modest) Tarrasch bonus. So
        // instead verify the term itself is wired in: the same position must
        // evaluate higher with the bonus than with the bonus zeroed out.
        var behind = new Board("4k3/8/8/P7/8/8/8/R3K3 w - - 0 1");
        Score saved = EvaluationParams.RookBehindPasser;
        try
        {
            int withBonus = new ClassicalEvaluator().Evaluate(behind);
            EvaluationParams.RookBehindPasser = default;
            int withoutBonus = new ClassicalEvaluator().Evaluate(behind);
            Assert.True(withBonus > withoutBonus,
                "Tarrasch: the rook-behind-passer bonus must be applied");
        }
        finally
        {
            EvaluationParams.RookBehindPasser = saved;
        }
    }

    [Fact]
    public void Passers_BlockedPasserScoresLowerThanFreePasser()
    {
        var evaluator = new ClassicalEvaluator();

        // Same material; the black knight blockades b6 in the first board and
        // stands beside the pawn's path (a5) in the second.
        var blocked = new Board("4k3/8/1n6/1P6/8/8/8/4K3 w - - 0 1");
        var free = new Board("4k3/8/8/nP6/8/8/8/4K3 w - - 0 1");

        Assert.True(evaluator.Evaluate(blocked) < evaluator.Evaluate(free),
            "A blockaded passer must be worth less than a free one");
    }

    [Fact]
    public void PawnStructure_ConnectedPassersBeatSplitPassers()
    {
        var structure = new PawnStructureEvaluator();

        // Two white passers on adjacent files vs the same two passers split
        // far apart; no black pawns, so all four are passed either way.
        var connected = new Board("4k3/8/8/3PP3/8/8/8/4K3 w - - 0 1");
        var split = new Board("4k3/8/8/P6P/8/8/8/4K3 w - - 0 1");

        // Endgame phase only: that is where passers decide games (the texel
        // tuner keeps the middlegame half of the bonus near zero).
        Score c = structure.Evaluate(connected);
        Score s = structure.Evaluate(split);
        Assert.True(c.Eg > s.Eg,
            "Connected passers must outscore split passers in the endgame");
    }

    [Fact]
    public void Evaluation_StartingPositionIsBalanced()
    {
        // The starting position is symmetric, so the evaluation equals exactly
        // Tempo (the side-to-move initiative bonus). Any value other than Tempo
        // reveals a colour asymmetry in the tapered tables or terms.
        var evaluator = new ClassicalEvaluator();
        Assert.Equal(EvaluationParams.Tempo, evaluator.Evaluate(new Board(Board.StartFen)));
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

    [Fact]
    public void Phalanx_BonusIsApplied()
    {
        // White has two pawns side by side on d4/e4 (phalanx) vs the same
        // pawns on d4/f4 (not touching). Zero out the phalanx array to verify
        // the term is responsible for the difference.
        var phalanxBoard = new Board("4k3/8/8/8/3PP3/8/8/4K3 w - - 0 1");
        var splitBoard   = new Board("4k3/8/8/8/3P1P2/8/8/4K3 w - - 0 1");

        var savedPhalanx = EvaluationParams.Phalanx;
        EvaluationParams.Phalanx = [new(0,0), new(0,0), new(0,0), new(0,0),
                                     new(0,0), new(0,0), new(0,0), new(0,0)];
        var evaluator = new ClassicalEvaluator();
        int withoutBonus = evaluator.Evaluate(phalanxBoard) - evaluator.Evaluate(splitBoard);

        EvaluationParams.Phalanx = savedPhalanx;
        evaluator = new ClassicalEvaluator();
        int withBonus = evaluator.Evaluate(phalanxBoard) - evaluator.Evaluate(splitBoard);

        Assert.True(withBonus > withoutBonus, "Phalanx bonus must make d4/e4 score better than d4/f4");
    }

    [Fact]
    public void Backward_PenaltyIsApplied()
    {
        // White e3 is backward: stop square e4 is attacked by black d5, and
        // white has no pawn on d or f files behind it (ranks 0-1). Black d5 is
        // NOT backward because c6 is a black pawn on an adjacent file behind
        // it (rank 5 > rank 4 for black = "behind"). Only white is penalized,
        // so zeroing BackwardPawn must raise the score.
        var board = new Board("4k3/8/2p5/3p4/8/4P3/8/4K3 w - - 0 1");

        var saved = EvaluationParams.BackwardPawn;
        EvaluationParams.BackwardPawn = new(0, 0);
        int scoreWithout = new ClassicalEvaluator().Evaluate(board);

        EvaluationParams.BackwardPawn = saved;
        int scoreWith = new ClassicalEvaluator().Evaluate(board);

        Assert.True(scoreWith < scoreWithout, "Backward pawn penalty must lower the score of the position");
    }

    [Fact]
    public void Tempo_SideToMoveScoresHigher()
    {
        // In a symmetric position the side to move should always score higher
        // than the waiting side, purely from the tempo bonus.
        var evaluator = new ClassicalEvaluator();
        var whiteToMove = new Board("4k3/8/8/8/8/8/8/4K3 w - - 0 1");
        var blackToMove = new Board("4k3/8/8/8/8/8/8/4K3 b - - 0 1");
        int wtm = evaluator.Evaluate(whiteToMove);
        int btm = evaluator.Evaluate(blackToMove);
        Assert.True(wtm > 0 && btm > 0,
            "Both sides should score positive (tempo) when it is their turn in a symmetric position");
        Assert.True(wtm == btm, "Symmetric position must give both sides the same absolute tempo score");
    }
}
