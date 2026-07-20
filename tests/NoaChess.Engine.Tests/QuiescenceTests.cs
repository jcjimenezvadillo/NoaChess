using NoaChess.Core;
using NoaChess.Engine;

namespace NoaChess.Engine.Tests;

// Quiescence correctness. Every case here is a position the OLD captures-only
// quiescence scored wrongly: it stood pat on a meaningless static eval while in
// check, generated only captures so quiet escapes did not exist, pruned the
// single defence by SEE, and never recognised mate or stalemate at the horizon.
//
// The positions are deliberately shallow so the verdict comes from quiescence
// rather than from a deep main search finding the truth some other way.
public class QuiescenceTests
{
    private readonly ChessEngine _engine = new();

    // Score reported for the side to move, in centipawns.
    private int Score(string fen, int depth) =>
        _engine.FindBestMove(new Board(fen), depth).Score;

    private static bool IsLegal(Board board, Move move) =>
        MoveGenerator.GenerateLegalMoves(board).Contains(move);

    [Fact]
    public void QuietKingMove_IsFoundWhenItIsTheOnlyEscape()
    {
        // Black king on h8 is checked by the rook on h1. There is no capture
        // and nothing to interpose: the only legal replies are quiet king
        // steps. A captures-only quiescence saw NO moves here.
        var board = new Board("7k/6p1/8/8/8/8/8/6KR b - - 0 1");
        var result = _engine.FindBestMove(board, depth: 2);

        Assert.True(IsLegal(board, result.BestMove));
        // Not mate, and not a lost-on-the-spot score: the king simply escapes.
        Assert.True(Math.Abs(result.Score) < 50_000,
            $"expected a normal score, got {result.Score}");
    }

    [Fact]
    public void Interposition_IsFoundWhenItIsTheOnlyEscape()
    {
        // White is checked by Ra1 along the first rank. The king cannot move
        // (g1 is on the rook's rank, g2/h2 are its own pawns) and there is
        // nothing to capture: the ONLY legal reply is the quiet interposition
        // Rd8-d1. A captures-only evasion stage cannot see this move at all.
        var board = new Board("3R4/4k3/8/8/8/8/6PP/r6K w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 2);

        Assert.Equal("d8d1", result.BestMove.ToString());
        Assert.True(IsLegal(board, result.BestMove));
    }

    [Fact]
    public void MateIsNotScoredAsAQuietPosition()
    {
        // Ra8# is a genuine back-rank mate: the rook is out of the king's
        // reach, f8/h8 stay on the rook's rank and f7/g7/h7 are black's own
        // pawns. (A rook landing on h8 instead would simply hang to Kxh8 —
        // the trap the first draft of this test fell into.)
        var board = new Board("6k1/5ppp/8/8/8/8/5PPP/R5K1 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 3);

        Assert.Equal("a1a8", result.BestMove.ToString());
        Assert.True(result.Score > 50_000,
            $"expected a mate score for white, got {result.Score}");
    }

    [Fact]
    public void BeingMatedIsReportedAsLoss_NotAsStandPat()
    {
        // The same mate, already delivered, with black to move: no escape
        // square and no way to touch the rook on a8. Standing pat here would
        // report ordinary material for a lost game.
        var board = new Board("R5k1/5ppp/8/8/8/8/5PPP/6K1 b - - 0 1");
        var result = _engine.FindBestMove(board, depth: 2);

        Assert.True(result.Score < -50_000,
            $"expected a mated score for black, got {result.Score}");
    }

    [Fact]
    public void SingleDefenceIsNotPrunedBySee()
    {
        // White is checked by Re1 and has exactly ONE legal reply: Qxe1,
        // which drops the queen to Bxe1 (SEE 500 - 900 = -400). f1 and h1 are
        // both on the rook's rank, f2/g2/h2 are white's own pawns, and no
        // white piece can interpose on f1.
        // The bishop recaptures from a5 (its path opens as the queen leaves
        // d2) and does NOT attack g1 from e1 — so white survives a piece down
        // instead of being mated. A quiescence that applies SEE pruning while
        // in check throws this move away and invents a mate.
        var board = new Board("k7/8/8/b7/8/8/3Q1PPP/4r1K1 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 2);

        Assert.Equal("d2e1", result.BestMove.ToString());
        Assert.True(result.Score > -50_000,
            $"expected a non-mate score, got {result.Score}");
    }

    [Fact]
    public void StalemateAtTheHorizonScoresAsDraw()
    {
        // Classic stalemate: black king a8, white king a6, white queen c7.
        // Black to move has no legal move and is NOT in check — the score
        // must be 0, not the crushing material advantage white owns.
        var board = new Board("k7/2Q5/K7/8/8/8/8/8 b - - 0 1");
        var result = _engine.FindBestMove(board, depth: 2);

        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void PerpetualCheckSequenceTerminates()
    {
        // A position where checks can repeat forever. Quiescence now searches
        // quiet evasions, so without repetition detection this could spin to
        // the ply ceiling; the search must simply come back with a legal move.
        var board = new Board("6k1/6p1/8/8/8/8/1q6/1K6 w - - 0 1");
        var result = _engine.FindBestMove(board, depth: 6);

        Assert.True(IsLegal(board, result.BestMove));
    }

    [Fact]
    public void CaptureGivingCheck_IsEvaluatedThroughTheEvasion()
    {
        // Quiet Italian position where Bxf7+ is available: a capture that
        // gives check. The node it leads to is exactly where the old
        // quiescence was broken — it stood pat on a checked position — and
        // the sacrifice is unsound here (Kxf7 just wins the bishop), so the
        // engine must decline it and stay near equality.
        // (An earlier draft used a Scholar's-mate position by accident, where
        // Qxf7 IS mate and the "phantom win" was the truth.)
        var board = new Board("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/3P1N2/PPP2PPP/RNBQK2R w KQkq - 0 1");
        var result = _engine.FindBestMove(board, depth: 4);

        Assert.True(IsLegal(board, result.BestMove));
        Assert.True(Math.Abs(result.Score) < 50_000,
            $"expected a normal score, got {result.Score}");
        Assert.NotEqual("c4f7", result.BestMove.ToString());
    }
}
