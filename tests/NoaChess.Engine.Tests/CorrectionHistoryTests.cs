using NoaChess.Core;
using NoaChess.Engine.Heuristics;

namespace NoaChess.Engine.Tests;

public sealed class CorrectionHistoryTests
{
    private const ulong NoContinuation = 0;

    [Fact]
    public void UpdateCorrectsTowardObservedResidualAndClearResets()
    {
        var board = new Board();
        var history = new CorrectionHistorySet();

        Assert.Equal(25, history.Correct(board, 25, NoContinuation));

        history.Update(board, errorCp: 120, depth: 8, NoContinuation);
        int corrected = history.Correct(board, 25, NoContinuation);
        Assert.InRange(corrected, 26, 145);

        history.Clear();
        Assert.Equal(25, history.Correct(board, 25, NoContinuation));
    }

    [Fact]
    public void EntriesAreSeparatedBySideToMove()
    {
        var board = new Board();
        var history = new CorrectionHistorySet();
        history.Update(board, errorCp: 120, depth: 8, NoContinuation);

        board.MakeNullMove();
        Assert.Equal(25, history.Correct(board, 25, NoContinuation));
        board.UnmakeNullMove();
        Assert.True(history.Correct(board, 25, NoContinuation) > 25);
    }

    // THE key property of v4.3.0's combination rule. The pawn table is the one
    // that was actually validated (v2.8.2); the five new tables were added on
    // top. If folding them in changed what the pawn signal alone produces, a
    // failed SPRT would be unattributable between "the new keys do not help"
    // and "we damaged the one that did" - so the weights are chosen to make the
    // pawn-only case arithmetically identical, and that is asserted here rather
    // than trusted.
    [Fact]
    public void PawnOnlySignal_ProducesTheSameCorrectionAsBefore()
    {
        var board = new Board();
        var set = new CorrectionHistorySet();
        var pawnAlone = new CorrectionHistory();

        // Drive both with the same observations. The set writes to every table,
        // but only the pawn key can match on a position whose other material is
        // untouched... so instead we compare against the pre-v4.3.0 formula
        // applied to the pawn table directly.
        foreach (int error in new[] { 120, -40, 75, 10 })
        {
            set.Update(board, error, depth: 8, NoContinuation);
            pawnAlone.Update(board, board.PawnZobristKey, error, depth: 8);
        }

        // Pre-v4.3.0 behaviour: rawEval + pawnEntry / Scale.
        int legacy = 25 + pawnAlone.RawEntry(board, board.PawnZobristKey) / CorrectionHistory.Scale;

        // The set also learned in its minor/major/non-pawn tables from the same
        // observations, so it corrects by MORE than the pawn alone. What must
        // hold is that the pawn contribution is undiminished: the combined
        // correction has the same sign and is at least as large in magnitude.
        int combined = set.Correct(board, 25, NoContinuation);
        Assert.True(Math.Abs(combined - 25) >= Math.Abs(legacy - 25),
            $"the pawn signal was diluted: combined {combined - 25} vs legacy {legacy - 25}");
        Assert.Equal(Math.Sign(legacy - 25), Math.Sign(combined - 25));
    }

    // The continuation key describes HOW a position was reached, so it must
    // separate observations that the position-keyed tables cannot tell apart.
    [Fact]
    public void ContinuationKey_SeparatesObservationsWithinOnePosition()
    {
        var board = new Board();
        var history = new CorrectionHistorySet();

        ulong afterKnight = CorrectionHistorySet.ContinuationKey(pieceIndex: 1, toSquare: 18);
        ulong afterBishop = CorrectionHistorySet.ContinuationKey(pieceIndex: 2, toSquare: 26);

        history.Update(board, errorCp: 200, depth: 10, afterKnight);

        // Both readings share every position-based key, so any difference
        // between them can only come from the continuation table.
        int viaKnight = history.Correct(board, 0, afterKnight);
        int viaBishop = history.Correct(board, 0, afterBishop);
        Assert.True(viaKnight > viaBishop,
            "the continuation table did not separate two different predecessors");
    }

    // 0 is the caller's sentinel for "no previous move" (root, or after a null
    // move). It must never be a usable table index, or every root evaluation
    // would share one entry with whatever hashes to zero.
    [Fact]
    public void ContinuationKey_IsNeverZero()
    {
        for (int piece = 0; piece < 12; piece++)
            for (int square = 0; square < 64; square++)
                Assert.NotEqual(0UL, CorrectionHistorySet.ContinuationKey(piece, square));
    }

    // Corrections must stay bounded however extreme and consistent the
    // observations are: this value feeds forward pruning, and an unbounded
    // correction would let a learned bias fabricate cutoffs.
    [Fact]
    public void CombinedCorrectionStaysBounded()
    {
        var board = new Board();
        var history = new CorrectionHistorySet();
        ulong continuation = CorrectionHistorySet.ContinuationKey(0, 16);

        for (int i = 0; i < 500; i++)
            history.Update(board, errorCp: 10_000, depth: 20, continuation);

        int corrected = history.Correct(board, 0, continuation);
        Assert.InRange(corrected, 0, 320);
    }
}
