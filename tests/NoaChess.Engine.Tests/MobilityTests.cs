using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Engine.Tests;

// Tests of the 4C mobility upgrade: SF mobility area, pinned-piece attack
// restriction and x-ray attacks (bishops through queens, rooks through
// queens and own rooks). All probed through the attackedBy / mobility-area
// accessors after a full Evaluate() call.
public class MobilityTests
{
    private static int Sq(string algebraic) =>
        (algebraic[0] - 'a') + (algebraic[1] - '1') * 8;

    private static ulong Bb(string algebraic) => 1UL << Sq(algebraic);

    private static ClassicalEvaluator Evaluated(string fen, out Board board)
    {
        var evaluator = new ClassicalEvaluator();
        board = new Board(fen);
        evaluator.Evaluate(board);
        return evaluator;
    }

    [Fact]
    public void PinnedBishop_AttacksRestrictedToPinLine()
    {
        // White bishop e3 is pinned vertically by the black rook e8 against
        // the king e1. A bishop has no attacks along a file, so its effective
        // attack set must be empty.
        var evaluator = Evaluated("4r1k1/8/8/8/8/4B3/8/4K3 w - - 0 1", out _);
        Assert.Equal(0UL, evaluator.AttackedBy(Color.White, PieceType.Bishop));
    }

    [Fact]
    public void PinnedRook_KeepsOnlyPinLineAttacks()
    {
        // White rook e3 pinned by the rook e8 against the king e1: it still
        // attacks along the e-file (both ways) but no longer along rank 3.
        var evaluator = Evaluated("4r1k1/8/8/8/8/4R3/8/4K3 w - - 0 1", out _);
        ulong rook = evaluator.AttackedBy(Color.White, PieceType.Rook);
        Assert.NotEqual(0UL, rook & Bb("e5"));
        Assert.Equal(0UL, rook & (Bb("a3") | Bb("h3")));
    }

    [Fact]
    public void Rook_XraysThroughOwnRookOnly()
    {
        // Doubled white rooks d1+d3: the back rook sees through its partner
        // to the top of the file. An own KNIGHT on d3 instead must block it.
        var xray = Evaluated("3qk3/8/8/8/8/3R4/8/3RK3 w - - 0 1", out _);
        Assert.NotEqual(0UL, xray.AttackedBy(Color.White, PieceType.Rook) & Bb("d8"));

        var blocked = Evaluated("3qk3/8/8/8/8/3N4/8/3RK3 w - - 0 1", out _);
        // d1-rook stops at d3; only the knight covers beyond, not the rook.
        Assert.Equal(0UL, blocked.AttackedBy(Color.White, PieceType.Rook) & Bb("d8"));
    }

    [Fact]
    public void Bishop_XraysThroughQueens()
    {
        // White bishop c1 with the white queen on d2: the bishop sees through
        // the queen along the diagonal to h6.
        var evaluator = Evaluated("4k3/8/8/8/8/8/3Q4/2B1K3 w - - 0 1", out _);
        Assert.NotEqual(0UL, evaluator.AttackedBy(Color.White, PieceType.Bishop) & Bb("h6"));
    }

    [Fact]
    public void MobilityArea_ExcludesKingQueenAndLowBlockedPawns()
    {
        // Starting position: all white pawns sit on the low ranks (excluded),
        // the king and queen squares are excluded, and rank-6 squares covered
        // by black pawns are excluded too.
        var evaluator = Evaluated("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", out _);
        ulong area = evaluator.MobilityArea(Color.White);

        Assert.Equal(0UL, area & (Bb("e1") | Bb("d1")));         // own K and Q
        Assert.Equal(0UL, area & (Bb("a2") | Bb("e2") | Bb("h2"))); // low pawns
        Assert.Equal(0UL, area & (Bb("b6") | Bb("f6")));          // enemy pawn control
        Assert.NotEqual(0UL, area & Bb("f3"));                     // normal square stays
    }

    [Fact]
    public void MobilityArea_ExcludesPinnedPieces()
    {
        // The white knight d2 is pinned by the black bishop a5 against the
        // king e1 (diagonal a5-e1): its square leaves the mobility area and
        // it is flagged as a blocker for the king.
        var evaluator = Evaluated("4k3/8/8/b7/8/8/3N4/4K3 w - - 0 1", out _);
        Assert.NotEqual(0UL, evaluator.BlockersForKing(Color.White) & Bb("d2"));
        Assert.Equal(0UL, evaluator.MobilityArea(Color.White) & Bb("d2"));
    }

    [Fact]
    public void Mobility_CagedKnightIsPenalizedNonLinearly()
    {
        // Direct table sanity: the SF curve is steep at the caged end and
        // flat at the top (knight: 0 moves -30 mg, 4 moves +1, 8 moves +18).
        Score caged = EvaluationParams.MobilityBonus[0][0];
        Score mid = EvaluationParams.MobilityBonus[0][4];
        Score full = EvaluationParams.MobilityBonus[0][8];

        int lowGainMg = mid.Mg - caged.Mg;   // 0 -> 4 squares
        int highGainMg = full.Mg - mid.Mg;   // 4 -> 8 squares
        int lowGainEg = mid.Eg - caged.Eg;
        int highGainEg = full.Eg - mid.Eg;
        Assert.True(lowGainMg > highGainMg && lowGainEg > 2 * highGainEg,
            $"The caged end of the curve must be steeper: " +
            $"mg {lowGainMg} vs {highGainMg}, eg {lowGainEg} vs {highGainEg}");
    }
}
