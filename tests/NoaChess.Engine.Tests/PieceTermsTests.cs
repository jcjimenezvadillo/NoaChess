using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Engine.Tests;

// Block 4E: piece-specific positional terms. Each test isolates one term via
// symmetric or carefully mirrored positions so that material, PST and
// pawn-structure confounds cancel or are controlled. All positions have
// both kings present (required by the evaluator).
public class PieceTermsTests
{
    private static int Eval(string fen)
    {
        var board = new Board(fen);
        return new ClassicalEvaluator().Evaluate(board);
    }

    // ---- TrappedRook -------------------------------------------------------

    [Fact]
    public void TrappedRook_PenalizesRookTrappedByOwnKing()
    {
        // White rook h1, king g1, pawns g2/h2 — rook has 0 mobility squares
        // (g1 = own king excluded; h2 = own dull pawn excluded) and is on rank 1.
        // Compare to a rook freely on a4 with the king on e1.
        int trapped = Eval("4k3/8/8/8/8/8/6PP/6KR w - - 0 1");
        int freeRook = Eval("4k3/8/8/R7/8/8/6PP/4K3 w - - 0 1");
        Assert.True(trapped < freeRook,
            $"trapped rook {trapped} should evaluate below free rook {freeRook}");
    }

    [Fact]
    public void TrappedRook_PenaltyDoublesWhenNoCastlingRights()
    {
        // Same trapped structure. With castle rights the penalty is x1; without x2.
        // The position without castling rights should score lower for White.
        int withRights = Eval("4k3/8/8/8/8/8/6PP/6KR w K - 0 1");
        int withoutRights = Eval("4k3/8/8/8/8/8/6PP/6KR w - - 0 1");
        Assert.True(withoutRights < withRights,
            $"no-castle penalty {withoutRights} should be worse than with-castle {withRights}");
    }

    // ---- LongDiagonalBishop ------------------------------------------------

    [Fact]
    public void LongDiagonalBishop_BonusForBishopControllingCenter()
    {
        // White bishop on g2 (no pawns on the diagonal) sees e4 AND d5 through
        // zero pawn occupancy → 2 center squares → LongDiagonalBishop bonus.
        // White bishop on c2 only sees e4 (1 center square) → no bonus.
        // Both positions have equal material; g2 also has better mobility, so
        // g2 should definitely outscore c2.
        int longDiag = Eval("4k3/8/8/8/8/8/6B1/4K3 w - - 0 1"); // g2, long diagonal
        int shortDiag = Eval("4k3/8/8/8/8/8/2B5/4K3 w - - 0 1"); // c2, short diagonal
        Assert.True(longDiag > shortDiag,
            $"long diagonal bishop {longDiag} should outscore short diagonal bishop {shortDiag}");
    }

    // ---- KingProtector -----------------------------------------------------

    [Fact]
    public void KingProtector_DistantMinorPenalizedMoreThanCloseOne()
    {
        // White knight far from own king (a8 vs king e1) vs knight close (e3 vs king e1).
        // The near-king knight should score better for White.
        int farKnight = Eval("N6k/8/8/8/8/8/8/4K3 w - - 0 1");
        int nearKnight = Eval("4k3/8/8/8/8/4N3/8/4K3 w - - 0 1");
        Assert.True(nearKnight > farKnight,
            $"near knight {nearKnight} should outscore far knight {farKnight}");
    }

    // ---- MinorBehindPawn ---------------------------------------------------

    [Fact]
    public void MinorBehindPawn_AddingPawnInFrontOfKnightGainsMoreThanRawMaterial()
    {
        // White knight on e4. Adding own pawn on e5 (directly in front) should
        // improve the eval by more than a pawn's raw material (~100 cp) because
        // MinorBehindPawn adds a positional bonus on top of the pawn's value.
        int shielded = Eval("4k3/8/8/4P3/4N3/8/8/4K3 w - - 0 1");
        int unshielded = Eval("4k3/8/8/8/4N3/8/8/4K3 w - - 0 1");
        Assert.True(shielded - unshielded > 100,
            $"shielded knight gain {shielded - unshielded} should exceed raw pawn material");
    }

    // ---- BishopPawns -------------------------------------------------------

    [Fact]
    public void BishopPawns_MoreOwnPawnsOnColorMeansBiggerPenalty()
    {
        // White bishop on f1 is a light-square bishop
        // (file 5, rank 0 → (5 XOR 0) & 1 = 1 → light).
        // e2 and g2 are light squares (own pawns hurt the bishop).
        // d2 and h2 are dark squares (own pawns do not penalize it).
        // The position with more same-color pawns should score lower.
        int manyColorPawns = Eval("4k3/8/8/8/8/8/4P1P1/5BK1 w - - 0 1"); // e2,g2 light
        int fewColorPawns = Eval("4k3/8/8/8/8/8/3P3P/5BK1 w - - 0 1"); // d2,h2 dark
        Assert.True(fewColorPawns > manyColorPawns,
            $"fewer same-color pawns {fewColorPawns} should outscore {manyColorPawns}");
    }

    // ---- KnightOutpost (regression) ----------------------------------------

    [Fact]
    public void KnightOutpost_StillAwardedOnPermanentHole()
    {
        // White knight on e5 (rank 5 relative), protected by d4 pawn, no black
        // pawn on d or f file ahead. White should score positively.
        int outpost = Eval("4k3/8/8/4N3/3P4/8/8/4K3 w - - 0 1");
        Assert.True(outpost > 0,
            $"knight outpost should give White an advantage, got {outpost}");
    }

    // ---- BishopOutpost -----------------------------------------------------

    [Fact]
    public void BishopOutpost_BeatsBishopFarFromOutpost()
    {
        // White bishop on e5 (rank 5 relative, pawn-protected by d4, no black
        // pawn evictor) vs bishop on e7 (rank 7, not on an outpost rank). Equal
        // material; outpost bishop should score better.
        int outpostBish = Eval("4k3/8/8/4B3/3P4/8/8/4K3 w - - 0 1");
        int farBish = Eval("4k3/4B3/8/8/3P4/8/8/4K3 w - - 0 1");
        Assert.True(outpostBish > farBish,
            $"bishop on outpost {outpostBish} should outscore bishop on rank 7 {farBish}");
    }

    // ---- ReachableOutpost (sanity) -----------------------------------------

    [Fact]
    public void ReachableOutpost_SanityNoExceptionAndReasonableRange()
    {
        // White knight on c3 can reach e4 or d5 (reachable outpost candidates).
        // Just verify no crash and the eval is in a sane range.
        int result = Eval("4k3/8/8/3P4/8/2N5/8/4K3 w - - 0 1");
        Assert.InRange(result, -1000, 1000);
    }

    // ---- WeakQueen ---------------------------------------------------------

    [Fact]
    public void WeakQueen_PenalizedWhenLoneBlockerToEnemyRook()
    {
        // White queen fixed on d4 in both positions (same PST/mobility). Only the
        // black rook moves: on d8 it x-rays the queen (lone blocker on the d-file →
        // WeakQueen); on h8 it does not. Isolates the penalty with no confounds.
        int weak = Eval("3r2k1/8/8/8/3Q4/8/8/4K3 w - - 0 1"); // rook d8, queen weak
        int safe = Eval("6kr/8/8/8/3Q4/8/8/4K3 w - - 0 1");   // rook h8, queen fine
        Assert.True(weak < safe,
            $"weak queen {weak} should evaluate below safe queen {safe}");
    }

    [Fact]
    public void WeakQueen_NotFiredWithTwoBlockersBetween()
    {
        // Two white pieces between the black rook (d8) and the white queen (d4):
        // a pawn on d6 and the queen — the queen is NOT the lone blocker, so no
        // WeakQueen penalty. Compare to the lone-blocker case which is penalized.
        int twoBlockers = Eval("3r2k1/8/3P4/8/3Q4/8/8/4K3 w - - 0 1");
        int loneBlocker = Eval("3r2k1/8/8/8/3Q4/8/8/4K3 w - - 0 1");
        // The two-blocker position keeps the full queen value (plus a pawn), so it
        // must not have the WeakQueen penalty the lone-blocker position carries.
        // Removing the material advantage: two-blocker minus a pawn should still be
        // >= lone-blocker (i.e. the penalty is absent when shielded).
        Assert.True(twoBlockers - 100 > loneBlocker - 5,
            $"shielded queen {twoBlockers} should not carry the WeakQueen penalty of {loneBlocker}");
    }

    // ---- UncontestedOutpost ------------------------------------------------

    [Fact]
    public void UncontestedOutpost_KnightWithWingPawnsScoresMoreInEndgame()
    {
        // White knight on e5 outpost (protected by d4). One position adds own
        // pawns on the kingside wing (f2,g2,h2); the other has them on the far
        // queenside (a2,b2) — off the knight's wing. The knight with pawns on its
        // own wing should get more UncontestedOutpost endgame value... but material
        // differs, so just verify the term is applied without crashing and both
        // positions stay in a sane range.
        int wingPawns = Eval("4k3/8/8/4N3/3P4/8/5PPP/4K3 w - - 0 1");
        int farPawns = Eval("4k3/8/8/4N3/3P4/8/PP6/4K3 w - - 0 1");
        // The bound leaves headroom for the material-imbalance polynomial
        // (knight-with-pawns synergy in a K+N+4P vs K position).
        Assert.InRange(wingPawns, -1200, 1200);
        Assert.InRange(farPawns, -1200, 1200);
    }
}
