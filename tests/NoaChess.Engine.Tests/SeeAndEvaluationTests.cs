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

        Assert.True(structure.Evaluate(board) < 0,
            "Doubled+isolated pawns vs connected pawns must score negatively");
    }
}
