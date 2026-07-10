using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Engine.Tests;

// Tests of the attackedBy / attackedBy2 infrastructure (Bloque 4A), the
// prerequisite for the threat, king-safety and mobility evaluation terms.
public class AttackedByTests
{
    private static int Sq(string algebraic) =>
        (algebraic[0] - 'a') + (algebraic[1] - '1') * 8;

    private static ulong Bb(string algebraic) => 1UL << Sq(algebraic);

    [Fact]
    public void AttackedBy_AllPieces_IsUnionOfEveryType()
    {
        var evaluator = new ClassicalEvaluator();
        var board = new Board(); // starting position
        evaluator.Evaluate(board);

        for (int c = 0; c < 2; c++)
        {
            var color = (Color)c;
            ulong union = 0;
            for (int p = 0; p < 6; p++)
                union |= evaluator.AttackedBy(color, (PieceType)p);
            Assert.Equal(union, evaluator.AttackedByAll(color));
        }
    }

    [Fact]
    public void AttackedBy_StartPosition_PawnsCoverThirdRank()
    {
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board());

        Assert.Equal(0xFFUL << 16, evaluator.AttackedBy(Color.White, PieceType.Pawn));
        Assert.Equal(0xFFUL << 40, evaluator.AttackedBy(Color.Black, PieceType.Pawn));
    }

    [Fact]
    public void AttackedBy2_PawnDoubleAttack_IsCounted()
    {
        // Pawns b2 and d2 both attack c3: a double attack from pawns alone.
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board("4k3/8/8/8/8/8/1P1P4/4K3 w - - 0 1"));

        Assert.NotEqual(0UL, evaluator.AttackedBy2(Color.White) & Bb("c3"));
    }

    [Fact]
    public void AttackedBy2_TwoRooksOnSameRank_OverlapIsDouble()
    {
        // Rooks a3 and h3 both sweep the third rank: b3..g3 are double attacks.
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board("4k3/8/8/8/8/R6R/8/4K3 w - - 0 1"));

        ulong overlap = Bb("b3") | Bb("c3") | Bb("d3") | Bb("e3") | Bb("f3") | Bb("g3");
        Assert.Equal(overlap, evaluator.AttackedBy2(Color.White) & overlap);
    }

    [Fact]
    public void AttackedBy2_SingleAttacker_IsNotDouble()
    {
        // A lone knight's attacks are single attacks; only the squares shared
        // with its own king may be double.
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board("4k3/8/8/8/8/8/8/N3K3 w - - 0 1"));

        // Knight a1 attacks b3 and c2; neither is touched by the e1 king.
        Assert.Equal(0UL, evaluator.AttackedBy2(Color.White) & (Bb("b3") | Bb("c2")));
    }

    [Fact]
    public void AttackedBy_KingAndPawnOverlap_IsDouble()
    {
        // Pawn f2 attacks e3/g3 and knight h1 attacks f2/g3: g3 is a double
        // attack built from the initialize seed (pawn) plus the piece loop.
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board("4k3/8/8/8/8/8/5P2/4K2N w - - 0 1"));

        Assert.NotEqual(0UL, evaluator.AttackedBy2(Color.White) & Bb("g3"));
    }

    [Fact]
    public void AttackedBy_IsRebuiltBetweenCalls_NoStaleState()
    {
        // Evaluating a queen-heavy position and then a bare-kings position must
        // not leak attack bitboards from the first call into the second.
        var evaluator = new ClassicalEvaluator();
        evaluator.Evaluate(new Board("3qk3/8/8/8/8/8/8/3QK3 w - - 0 1"));
        evaluator.Evaluate(new Board("4k3/8/8/8/8/8/8/4K3 w - - 0 1"));

        Assert.Equal(0UL, evaluator.AttackedBy(Color.White, PieceType.Queen));
        Assert.Equal(0UL, evaluator.AttackedBy(Color.Black, PieceType.Queen));
        ulong expectedWhite = Attacks.King(Sq("e1"));
        Assert.Equal(expectedWhite, evaluator.AttackedByAll(Color.White)
                                  & ~evaluator.AttackedBy(Color.White, PieceType.Pawn));
    }
}
