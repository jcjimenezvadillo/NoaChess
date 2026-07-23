using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;

namespace NoaChess.Engine.Tests;

// Block 4F: reference passed-pawn terms — king proximity to the block square,
// the path-to-queen safety ladder, the PassedFile edge penalty and the
// blocked-passer filter. Positions are built so material and PSTs are
// controlled and only the probed term differs.
public class PassedPawnTermsTests
{
    private static int Eval(string fen)
    {
        var board = new Board(fen);
        return new ClassicalEvaluator().Evaluate(board);
    }

    // ---- King proximity ----------------------------------------------------

    [Fact]
    public void Passer_WorthMoreWhenEnemyKingIsFar()
    {
        // White passer on e5. Black king close to the block square (e7) vs far
        // away (h8, cornered). Same material.
        int kingFar = Eval("7k/8/8/4P3/8/8/8/4K3 w - - 0 1");
        int kingNear = Eval("8/4k3/8/4P3/8/8/8/4K3 w - - 0 1");
        Assert.True(kingFar > kingNear,
            $"far enemy king {kingFar} should beat near enemy king {kingNear}");
    }

    [Fact]
    public void Passer_WorthMoreWhenOwnKingEscorts()
    {
        // White passer on e5, own king escorting on d5 vs stuck on e1.
        // Black king pinned at h8 in both.
        int escorted = Eval("7k/8/8/3KP3/8/8/8/8 w - - 0 1");
        int abandoned = Eval("7k/8/8/4P3/8/8/8/4K3 w - - 0 1");
        Assert.True(escorted > abandoned,
            $"escorted passer {escorted} should beat abandoned passer {abandoned}");
    }

    // ---- Path-to-queen safety ladder ----------------------------------------

    [Fact]
    public void Passer_FreePathBeatsPathCoveredByEnemyRook()
    {
        // White passer e5, kings mirrored. A black rook on e8 covers the whole
        // path (k drops from 36 toward 0) vs the same rook on a1 (behind
        // nothing, path free). Material identical.
        int freePath = Eval("7k/8/8/4P3/8/8/8/r3K3 w - - 0 1");
        int guardedPath = Eval("4r2k/8/8/4P3/8/8/8/4K3 w - - 0 1");
        Assert.True(freePath > guardedPath,
            $"free path {freePath} should beat rook-guarded path {guardedPath}");
    }

    [Fact]
    public void Passer_OwnRookBehindOutweighsEnemyRookBehind()
    {
        // Rooks behind the passer: ours pushes it (k+5 plus the Tarrasch
        // bonus), theirs contests the whole path.
        int ownBehind = Eval("7k/8/8/4P3/8/8/8/4R2K w - - 0 1");
        int enemyBehind = Eval("7k/8/8/4P3/8/8/8/4r2K w - - 0 1");
        Assert.True(ownBehind > enemyBehind,
            $"own rook behind {ownBehind} should beat enemy rook behind {enemyBehind}");
    }

    // ---- PassedFile ----------------------------------------------------------

    [Fact]
    public void Passer_EdgeFileWorthMoreThanCentral()
    {
        // Identical passers on a5 vs d5 (kings far on the other wing so king
        // proximity is comparable). The central passer pays 3 x PassedFile.
        int edge = Eval("7k/8/8/P7/8/8/8/7K w - - 0 1");
        int central = Eval("7k/8/8/3P4/8/8/8/7K w - - 0 1");
        Assert.True(edge > central,
            $"edge passer {edge} should beat central passer {central} (PassedFile)");
    }

    // ---- Blocked-passer filter ----------------------------------------------

    [Fact]
    public void BlockedPasser_WithoutHelpLosesTheRankBonus()
    {
        // e5/e6 pawn ram: White's e5 "candidate passer" is blocked by the
        // black e6 pawn with no friendly pawn able to help — the filter takes
        // back the rank bonus. Compare with the same pawn truly passed
        // (black pawn moved to h6, not in the cone... use a3 far away).
        int blockedNoHelp = Eval("7k/8/4p3/4P3/8/8/8/7K w - - 0 1");
        int trulyPassed = Eval("7k/8/7p/4P3/8/8/8/7K w - - 0 1");
        Assert.True(trulyPassed > blockedNoHelp,
            $"true passer {trulyPassed} should beat helpless blocked ram {blockedNoHelp}");
    }

    [Fact]
    public void BlockedPasser_KeepsBonusWhenAHelperCanTrade()
    {
        // Same e5/e6 ram. With a white pawn on d4 the candidate condition
        // holds (support pawn can offer the d5 trade to free e5) and the
        // blocked-passer filter keeps the bonus. With the pawn back on d3 it
        // is not yet supporting e5, so e5 is not a candidate at all. The
        // passer bonus dwarfs the one-step PST difference.
        int helped = Eval("7k/8/4p3/4P3/3P4/8/8/7K w - - 0 1");
        int notHelped = Eval("7k/8/4p3/4P3/8/3P4/8/7K w - - 0 1");
        Assert.True(helped > notHelped,
            $"helped ram {helped} should beat non-helped ram {notHelped}");
    }
}
