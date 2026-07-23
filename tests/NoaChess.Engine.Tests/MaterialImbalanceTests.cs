using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using Xunit;

namespace NoaChess.Engine.Tests;

// v2.6.8: polynomial material imbalance. The arithmetic is pinned by
// hand-computed expansions of the quadratic formula so a table typo or a
// loop-bound slip cannot pass silently.
public class MaterialImbalanceTests
{
    [Fact]
    public void SymmetricMaterial_IsExactlyZero()
    {
        var imbalance = new MaterialImbalance();
        Score start = imbalance.Compute(new Board(Board.StartFen));
        Assert.Equal(0, start.Mg);
        Assert.Equal(0, start.Eg);
    }

    [Fact]
    public void KnightWithPawns_HandComputedValue()
    {
        // White Kg1, Ne3, Pa2, Pb2 vs Black Kg8, Pa7.
        // White: pawn row 2*(37*2) = 148 mg; knight row
        // 1*(-49 + 249*2 + 106*1) = 555 mg -> 703. Black: 1*(37*1) = 37.
        // mg = (703-37)*3/100 = 19. Eg: white 2*(39*2) + 1*(-62 + 187*2 + 84)
        // = 156+396 = 552; black 39; eg = 513*3/100 = 15.
        var imbalance = new MaterialImbalance();
        Score s = imbalance.Compute(new Board("6k1/p7/8/8/8/4N3/PP6/6K1 w - - 0 1"));
        Assert.Equal(19, s.Mg);
        Assert.Equal(15, s.Eg);
    }

    [Fact]
    public void BishopPairDiagonalIsZeroed_OnlyInteractionsRemain()
    {
        // White Kg1, Bc3, Bf3, Pa2, Pb2 vs Black Kg8, Pa7, Pb7. The pair's own
        // diagonal is zero (the standalone BishopPair term owns it); what is
        // left is the pair/pawn and bishop/pawn interactions:
        // white pawns 2*(37*2 + 101*1) = 350; bishops 2*(118*2 + 59*2) = 708;
        // black pawns 2*(37*2 + 33*1) = 214. mg = (1058-214)*3/100 = 25.
        // Eg: white 2*(39*2 + 28) + 2*(137*2 + 44*2) = 212 + 724 = 936;
        // black 2*(39*2 + 30) = 216; eg = 720*3/100 = 21.
        var imbalance = new MaterialImbalance();
        Score s = imbalance.Compute(new Board("6k1/pp6/8/8/8/2B2B2/PP6/6K1 w - - 0 1"));
        Assert.Equal(25, s.Mg);
        Assert.Equal(21, s.Eg);
    }

    [Fact]
    public void MirroredPosition_IsTheExactNegation()
    {
        // Same material with the colors swapped must negate exactly (the
        // final division truncates toward zero for both signs).
        var imbalance = new MaterialImbalance();
        Score white = imbalance.Compute(new Board("6k1/p7/8/8/8/4N3/PP6/6K1 w - - 0 1"));
        Score black = imbalance.Compute(new Board("6k1/pp6/4n3/8/8/8/P7/6K1 w - - 0 1"));
        Assert.Equal(-white.Mg, black.Mg);
        Assert.Equal(-white.Eg, black.Eg);
    }

    [Fact]
    public void CachedResult_MatchesTheFirstComputation()
    {
        var imbalance = new MaterialImbalance();
        var board = new Board("6k1/p7/8/8/8/4N3/PP6/6K1 w - - 0 1");
        Score first = imbalance.Compute(board);
        Score second = imbalance.Compute(board);
        Assert.Equal(first.Mg, second.Mg);
        Assert.Equal(first.Eg, second.Eg);
    }
}
