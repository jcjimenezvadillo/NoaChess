using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using Xunit;

namespace NoaChess.Engine.Tests;

// 4G pawn-structure chain terms: WeakLever, WeakUnopposed, DoubledEarly and
// the blocked-pawn rank bonus. Each test zeroes the term under test and
// compares against the default value on the same position (fresh evaluator
// instances so the pawn cache cannot serve stale scores across param changes).
public class PawnChainTermsTests
{
    [Fact]
    public void WeakLever_UnsupportedPawnAttackedTwicePaysPenalty()
    {
        // White d4 is attacked by black c5 AND e5 (double lever) and has no
        // supporter: whichever way white recaptures after ...cxd4/...exd4,
        // white ends up down a pawn exchange.
        var board = new Board("4k3/8/8/2p1p3/3P4/8/8/4K3 w - - 0 1");

        var saved = EvaluationParams.WeakLever;
        EvaluationParams.WeakLever = new(0, 0);
        Score without = new PawnStructureEvaluator().Evaluate(board);

        EvaluationParams.WeakLever = saved;
        Score with_ = new PawnStructureEvaluator().Evaluate(board);

        Assert.True(with_.Eg < without.Eg,
            "A double-levered unsupported pawn must pay the WeakLever penalty");
    }

    [Fact]
    public void WeakUnopposed_IsolatedPawnOnFreeFilePaysExtra()
    {
        // White e4 is isolated and unopposed (no black pawn on the e-file):
        // it is a permanent rook target that can never be traded forward.
        var board = new Board("4k3/1pp5/8/8/4P3/8/8/4K3 w - - 0 1");

        var saved = EvaluationParams.WeakUnopposed;
        EvaluationParams.WeakUnopposed = new(0, 0);
        Score without = new PawnStructureEvaluator().Evaluate(board);

        EvaluationParams.WeakUnopposed = saved;
        Score with_ = new PawnStructureEvaluator().Evaluate(board);

        Assert.True(with_.Eg < without.Eg,
            "An isolated pawn on a free file must pay Isolated + WeakUnopposed");
    }

    [Fact]
    public void DoubledEarly_FiresOnlyWhileEnemyStructureIsFluid()
    {
        // White c2/c3 doubled; black's h7 is completely free (not rammed, not
        // restrained), so the early-doubling surcharge applies on top.
        var board = new Board("4k3/7p/8/8/8/2P5/2P5/4K3 w - - 0 1");

        var saved = EvaluationParams.DoubledEarly;
        EvaluationParams.DoubledEarly = new(0, 0);
        Score without = new PawnStructureEvaluator().Evaluate(board);

        EvaluationParams.DoubledEarly = saved;
        Score with_ = new PawnStructureEvaluator().Evaluate(board);

        Assert.True(with_.Mg < without.Mg,
            "Doubled pawns with a fluid enemy structure must pay DoubledEarly");

        // Same doubled pawns, but now white d4 rams black d5: an enemy pawn
        // is fixed, so DoubledEarly must NOT fire (zeroing it changes nothing).
        var fixedBoard = new Board("4k3/8/8/3p4/3P4/2P5/2P5/4K3 w - - 0 1");

        EvaluationParams.DoubledEarly = new(0, 0);
        Score fixedWithout = new PawnStructureEvaluator().Evaluate(fixedBoard);

        EvaluationParams.DoubledEarly = saved;
        Score fixedWith = new PawnStructureEvaluator().Evaluate(fixedBoard);

        Assert.Equal(fixedWithout.Mg, fixedWith.Mg);
        Assert.Equal(fixedWithout.Eg, fixedWith.Eg);
    }

    [Fact]
    public void BlockedPawn_Rank5RamIsScored()
    {
        // White e5 rammed by black e6: relative rank 5 blocked pawn — the
        // term (negative in both phases at rank 5) must move the score.
        var board = new Board("4k3/8/4p3/4P3/8/8/8/4K3 w - - 0 1");

        var saved = EvaluationParams.BlockedPawnRank[0];
        EvaluationParams.BlockedPawnRank[0] = new(0, 0);
        Score without = new PawnStructureEvaluator().Evaluate(board);

        EvaluationParams.BlockedPawnRank[0] = saved;
        Score with_ = new PawnStructureEvaluator().Evaluate(board);

        Assert.True(with_.Mg != without.Mg || with_.Eg != without.Eg,
            "A rank-5 rammed pawn must be scored by BlockedPawnRank");
        Assert.True(with_.Mg < without.Mg,
            "The rank-5 blocked-pawn value is a middlegame penalty");
    }

    [Fact]
    public void Connected_SupportedPawnBeatsUnsupported()
    {
        // c3 supports d4 (connected) vs c3+e5... keep it minimal: d4 with a
        // supporter on c3 versus d4 with the same pawn parked on b2 where it
        // neither supports nor forms a phalanx (b2 itself is isolated there
        // in neither case — both far from black's a7 anchor).
        var structure = new PawnStructureEvaluator();
        Score supported = structure.Evaluate(new Board("4k3/p7/8/8/3P4/2P5/8/4K3 w - - 0 1"));
        Score loose = structure.Evaluate(new Board("4k3/p7/8/8/3P4/8/1P6/4K3 w - - 0 1"));

        Assert.True(supported.Mg > loose.Mg,
            "A supported pawn chain must outscore two loose pawns");
    }
}
