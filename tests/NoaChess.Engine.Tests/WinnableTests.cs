using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using Xunit;

namespace NoaChess.Engine.Tests;

// v2.6.9: winnable / scale factors. Every scale-factor branch and the
// complexity arithmetic are pinned by hand-computed values so a threshold
// typo or a branch-order slip cannot pass silently.
public class WinnableTests
{
    private static int Npm(Board b, Color c) => Winnable.NonPawnMaterial(b, c);

    [Fact]
    public void NonPawnMaterial_SumsMinorsRooksQueens()
    {
        // White: N + B + R + Q = 357 + 399 + 603 + 1248 = 2607. Black: R = 603.
        var b = new Board("4k3/4r3/8/8/8/8/8/1NBRQ1K1 w - - 0 1");
        Assert.Equal(2607, Npm(b, Color.White));
        Assert.Equal(603, Npm(b, Color.Black));
    }

    [Fact]
    public void KBK_MaterialFactor_IsDeadDraw()
    {
        // Strong side has no pawns and less than a rook in total -> 0.
        var b = new Board("4k3/8/8/8/8/8/8/4KB2 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(0, sf);
    }

    [Fact]
    public void KRKB_MaterialFactor_IsNearDraw()
    {
        // Rook vs bare bishop: advantage 603-399 <= 399, weak side has at
        // most a minor -> 4.
        var b = new Board("4kb2/8/8/8/8/8/8/4KR2 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(4, sf);
    }

    [Fact]
    public void KRBKR_MaterialFactor_IsVeryDrawish()
    {
        // R+B vs R: advantage exactly a bishop, weak side above a minor -> 14.
        var b = new Board("4kr2/8/8/8/8/8/8/3KRB2 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(14, sf);
    }

    [Fact]
    public void PureOppositeBishops_ScalesByStrongPassers()
    {
        // Bf1 is light, Bd8 is dark, one bishop each and nothing else:
        // 18 + 4*1 passer = 22, minus 4 (all pawns on one flank) = 18.
        var b = new Board("3bk3/8/8/p7/PP6/8/8/4KB2 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, Bitboard.SquareBB(25) /* b4 */,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(18, sf);
    }

    [Fact]
    public void MixedOppositeBishops_ScalesByStrongUnits()
    {
        // Same OCB plus a white knight: not pure, so 22 + 3*units of the
        // strong side (K, B, N, Pa4, Pb4 = 5) = 37, minus 4 single-flank = 33.
        var b = new Board("3bk3/8/8/p7/PP6/8/3N4/4KB2 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(33, sf);
    }

    [Fact]
    public void SingleRookEndgame_OneFlank_WeakKingDefending()
    {
        // Rook each, equal pawns, all of White's pawns kingside, the black
        // king next to its own pawns: 36, minus 4 single-flank = 32.
        var b = new Board("r7/6k1/7p/6p1/6P1/7P/6K1/R7 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: false);
        Assert.Equal(32, sf);
    }

    [Fact]
    public void QueenVersusMinors_ScalesByQueenlessMinors()
    {
        // Lone queen against two minors: 37 + 3*2 = 43 (pawns on both
        // flanks, no extra reduction).
        var b = new Board("1nb3k1/p3p3/8/8/8/8/8/3Q2K1 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: true);
        Assert.Equal(43, sf);
    }

    [Fact]
    public void DefaultBranch_CapsByStrongPawnCount()
    {
        // Knight each, three white pawns on both flanks:
        // min(64, 36 + 7*3) = 57, no single-flank reduction.
        var b = new Board("6k1/p5p1/2n5/8/8/2N5/PP4P1/6K1 w - - 0 1");
        int sf = Winnable.ScaleFactor(b, Color.White, 0UL,
            Npm(b, Color.White), Npm(b, Color.Black), pawnsOnBothFlanks: true);
        Assert.Equal(57, sf);
    }

    [Fact]
    public void Apply_ComplexityAndInterpolation_HandComputed()
    {
        // Knights and pawns on both flanks, kings on g1/g8, no passers.
        // outflanking = 0 + (0-7) = -7; bothFlanks; no infiltration.
        // complexity = 12*5 + 9*(-7) + 21 - 110 = -92.
        // u = clamp((-92+50)*48/100, -200, 0) = -20 -> mg = 180.
        // v = max(-92*48/100, -300) = -44 -> eg = 256.
        // sf (default branch, 3 white pawns, both flanks) = 57.
        // (180*12 + 256*12*57/64) / 24 = (2160 + 2736) / 24 = 204.
        var b = new Board("6k1/p5p1/2n5/8/8/2N5/PP4P1/6K1 w - - 0 1");
        Assert.Equal(204, Winnable.Apply(b, new Score(200, 300), 12, 0UL, 0UL));
    }

    [Fact]
    public void Apply_AlmostUnwinnable_HandComputed()
    {
        // Rook ending, kings g2/g7 crossed (outflanking -5), all pawns on
        // the kingside -> almostUnwinnable.
        // complexity = 12*4 + 9*(-5) - 43 - 110 = -150.
        // u = clamp((-150+50)*48/100, -100, 0) = -48 -> mg = 52.
        // v = max(-150*48/100, -150) = -72 -> eg = 78.
        // sf (rook-endgame branch) = 36 - 4 = 32.
        // (52*6 + 78*18*32/64) / 24 = (312 + 702) / 24 = 42.
        var b = new Board("r7/6k1/7p/6p1/6P1/7P/6K1/R7 w - - 0 1");
        Assert.Equal(42, Winnable.Apply(b, new Score(100, 150), 6, 0UL, 0UL));
    }

    [Fact]
    public void Evaluate_KBK_IsNearDraw()
    {
        // Without the scale factor a bare extra bishop evaluates near +330;
        // with sf = 0 only the phase-weighted midgame residue survives.
        var eval = new ClassicalEvaluator();
        int v = eval.Evaluate(new Board("4k3/8/8/8/8/8/8/4KB2 w - - 0 1"));
        Assert.True(Math.Abs(v) < 100, $"KBK should be near draw, got {v}");
    }

    [Fact]
    public void Evaluate_MirroredPosition_IsSymmetric()
    {
        // Color-flipping the position and the side to move must give the
        // exact same evaluation (winnable is color-symmetric).
        var eval = new ClassicalEvaluator();
        int white = eval.Evaluate(new Board("3bk3/8/8/p7/PP6/8/8/4KB2 w - - 0 1"));
        int black = eval.Evaluate(new Board("4kb2/8/8/pp6/P7/8/8/3BK3 b - - 0 1"));
        Assert.Equal(white, black);
    }
}
