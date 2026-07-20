using NoaChess.Core;
using NoaChess.Engine.Tablebases;

namespace NoaChess.Engine.Tests;

// Syzygy probing is differentially tested: a wrong index does not throw, it
// returns a WRONG result that looks perfectly valid and the search then trusts
// it absolutely. These cases cover the positions whose truth is known by
// inspection; the bulk verification against an independent prober over
// thousands of random positions lives in the oracle harness (scripts), because
// it needs the tablebase files present.
public class SyzygyTests
{
    private const string TbPath = @"F:\Works\_______________CHESSTEST\syzygy";

    private static bool TbPresent => Directory.Exists(TbPath)
        && Directory.GetFiles(TbPath, "*.rtbw").Length > 0;

    private static void EnsureInit()
    {
        if (Syzygy.CurrentPath != TbPath)
            Syzygy.Init(TbPath);
    }

    [Fact]
    public void LoadsTablebasesAndReportsCardinality()
    {
        if (!TbPresent) return;   // Tablebases not installed on this machine
        EnsureInit();
        Assert.True(Syzygy.Available);
        Assert.Equal(5, Syzygy.Cardinality);
    }

    // Every expectation below was DERIVED from an independent prober rather
    // than reasoned by hand — three hand-written fixtures in the first draft
    // were wrong (two were illegal positions with the side not to move already
    // in check, one missed that Rxh1 simply wins the rook).
    [Theory]
    [InlineData("8/8/8/8/8/3k4/8/3KQ3 w - - 0 1", WdlScore.Win)]     // KQvK
    [InlineData("8/8/8/3k4/8/3K4/3B4/8 w - - 0 1", WdlScore.Draw)]   // KBvK, no mate possible
    [InlineData("8/8/8/3k4/8/3K4/3N4/8 w - - 0 1", WdlScore.Draw)]   // KNvK, no mate possible
    [InlineData("8/8/8/3k4/8/3K4/3R4/8 w - - 0 1", WdlScore.Win)]    // KRvK
    [InlineData("4k3/8/4K3/8/8/8/8/4R3 w - - 0 1", WdlScore.Win)]    // KRvK, king cut off
    [InlineData("8/8/4k3/8/8/4K3/8/R6r w - - 0 1", WdlScore.Win)]    // Rxh1 wins the rook
    [InlineData("8/8/8/8/8/8/1p6/1k1K4 w - - 0 1", WdlScore.Loss)]   // KvKP, the pawn queens
    public void ProbeWdl_MatchesKnownOutcomes(string fen, WdlScore expected)
    {
        if (!TbPresent) return;   // Tablebases not installed on this machine
        EnsureInit();
        var board = new Board(fen);
        Assert.True(Syzygy.ProbeWdl(board, out WdlScore score), "position should be in the TBs");
        Assert.Equal(expected, score);
    }

    [Theory]
    [InlineData("8/8/8/8/8/3k4/8/3KQ3 w - - 0 1", 9)]      // KQvK
    [InlineData("8/8/8/3k4/8/3K4/3R4/8 w - - 0 1", 23)]    // KRvK
    [InlineData("4k3/8/4K3/8/8/8/8/4R3 w - - 0 1", 5)]     // KRvK, king cut off
    [InlineData("8/8/4k3/8/8/4K3/8/R6r w - - 0 1", 1)]     // Rxh1 zeroes immediately
    [InlineData("8/8/8/3k4/8/3K4/3B4/8 w - - 0 1", 0)]     // Draw: no distance to store
    [InlineData("8/8/8/8/8/8/1p6/1k1K4 w - - 0 1", -4)]    // Lost: negative distance
    public void ProbeDtz_MatchesKnownDistances(string fen, int expected)
    {
        if (!TbPresent) return;   // Tablebases not installed on this machine
        EnsureInit();
        var board = new Board(fen);
        Assert.True(Syzygy.ProbeDtz(board, out int dtz), "position should be in the TBs");
        Assert.Equal(expected, dtz);
    }

    [Fact]
    public void ProbeWdl_RejectsPositionsOutsideTheTablebases()
    {
        if (!TbPresent) return;   // Tablebases not installed on this machine
        EnsureInit();
        // Opening position: far more men than any 5-piece table covers.
        var board = new Board();
        Assert.False(Syzygy.ProbeWdl(board, out _));
    }

    [Fact]
    public void ProbeWdl_RejectsPositionsWithCastlingRights()
    {
        if (!TbPresent) return;   // Tablebases not installed on this machine
        EnsureInit();
        // The tables know nothing about castling, so such a position must be
        // refused rather than answered from the wrong entry.
        var board = new Board("4k2r/8/8/8/8/8/8/4K3 b k - 0 1");
        Assert.False(Syzygy.ProbeWdl(board, out _));
    }

}
