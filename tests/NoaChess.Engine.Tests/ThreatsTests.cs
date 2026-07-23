using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Engine.Tests;

// Behavioral tests of the threat terms (Bloque 4B threat evaluation).
// Each test compares two positions that differ only in the threat feature
// under test, so PSTs and material stay as equal as possible.
public class ThreatsTests
{
    private static int Eval(string fen) => new ClassicalEvaluator().Evaluate(new Board(fen));

    [Fact]
    public void Hanging_UndefendedAttackedPieceIsWorseThanDefended()
    {
        // White rook e3 attacks the black knight e5. In the first position the
        // knight is completely undefended (hanging); in the second a pawn on
        // d6 defends it (strongly protected: no rook threat, no hanging).
        int hanging = Eval("4k3/8/8/4n3/8/4R3/8/4K3 b - - 0 1");
        int defended = Eval("4k3/8/3p4/4n3/8/4R3/8/4K3 b - - 0 1");

        // Both evals are from Black's perspective (side to move): the hanging
        // knight must make Black's position clearly worse.
        Assert.True(hanging < defended,
            $"Hanging knight should be worse for its owner: {hanging} vs {defended}");
    }

    [Fact]
    public void ThreatBySafePawn_PawnAttackingMinorIsRewarded()
    {
        // White pawn d4 (safe: not attacked) attacks the black knight on e5
        // versus the same knight standing out of reach on e6. White to move.
        int attacked = Eval("4k3/8/8/4n3/3P4/8/8/4K3 w - - 0 1");
        int notAttacked = Eval("4k3/8/4n3/8/3P4/8/8/4K3 w - - 0 1");

        Assert.True(attacked > notAttacked,
            $"Safe pawn attacking a minor should score higher: {attacked} vs {notAttacked}");
    }

    [Fact]
    public void ThreatByMinor_AttackOnRookOutweighsAttackOnPawn()
    {
        // A white knight forking pressure: attacking a black rook (b6 knight
        // attacks a8? use d5 knight attacking rook on c7) vs attacking only
        // a black pawn of the same-side structure.
        int onRook = Eval("4k3/2r5/8/3N4/8/8/8/4K3 w - - 0 1");
        int onPawn = Eval("4k3/2p5/8/3N4/8/8/8/4K3 w - - 0 1");

        // Material differs (rook vs pawn for Black), so compare the threat
        // delta indirectly: evaluate each against the same position with the
        // knight far away (h1) where it attacks nothing.
        int onRookBase = Eval("4k3/2r5/8/8/8/8/8/N3K3 w - - 0 1");
        int onPawnBase = Eval("4k3/2p5/8/8/8/8/8/N3K3 w - - 0 1");

        int rookThreatGain = onRook - onRookBase;
        int pawnThreatGain = onPawn - onPawnBase;
        Assert.True(rookThreatGain > pawnThreatGain,
            $"Minor threat on a rook should outweigh threat on a pawn: {rookThreatGain} vs {pawnThreatGain}");
    }

    // Probe the threat term in isolation (no PST/material confounds).
    private static Score ThreatsOf(string fen, Color color)
    {
        var evaluator = new ClassicalEvaluator();
        var board = new Board(fen);
        evaluator.Evaluate(board); // fills the attackedBy tables
        return evaluator.Threats(board, color);
    }

    [Fact]
    public void ThreatByPawnPush_PushableThreatIsRewarded()
    {
        // White pawn d3 can push to d4 (safe, unoccupied) and would then
        // attack the black knight on e5. With the knight on e7 instead, the
        // push threatens nothing — compare the white threat term directly.
        Score pushThreat = ThreatsOf("4k3/8/8/4n3/8/3P4/8/4K3 w - - 0 1", Color.White);
        Score noThreat = ThreatsOf("4k3/4n3/8/8/8/3P4/8/4K3 w - - 0 1", Color.White);

        Assert.True(pushThreat.Mg > noThreat.Mg && pushThreat.Eg > noThreat.Eg,
            $"A safe pawn push creating a threat should raise the threat term: " +
            $"({pushThreat.Mg},{pushThreat.Eg}) vs ({noThreat.Mg},{noThreat.Eg})");
    }

    [Fact]
    public void SliderOnQueen_DoubleAttackedLineToQueenIsRewarded()
    {
        // White rooks d1+d2 (doubled: d-file squares are attackedBy2) with the
        // black queen on d8: the rook battery threatens the queen along the
        // file. Compare with the queen on h8, off the battery's line.
        int onLine = Eval("3qk3/8/8/8/8/8/3R4/2KR4 w - - 0 1");
        int offLine = Eval("4k2q/8/8/8/8/8/3R4/2KR4 w - - 0 1");

        Assert.True(onLine > offLine,
            $"A doubled-rook line against the queen should score higher: {onLine} vs {offLine}");
    }

    [Fact]
    public void Threats_StronglyProtectedPieceIsNotWeak()
    {
        // Black knight e5 defended by pawn d6, attacked once by the rook: the
        // knight is strongly protected. Sliding the black pawn from d6 to a7
        // (same material) makes the knight weak. Evaluations are Black's view.
        int protectedKnight = Eval("4k3/8/3p4/4n3/8/4R3/8/4K3 b - - 0 1");
        int weakKnight = Eval("4k3/p7/8/4n3/8/4R3/8/4K3 b - - 0 1");

        Assert.True(protectedKnight > weakKnight,
            $"A pawn-protected knight should be safer than an unprotected one: {protectedKnight} vs {weakKnight}");
    }
}
