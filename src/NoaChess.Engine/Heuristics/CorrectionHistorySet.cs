using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// The full set of correction histories and the rule that combines them (v4.3.0).
//
// Each table estimates the SAME quantity - the residual between static
// evaluation and searched score - from a different structural key, so the
// combination is a weighted average rather than a sum. Summing independent
// estimates of one quantity would over-correct whenever they agreed, which is
// exactly when they are most trustworthy.
//
// THE WEIGHTS ARE CHOSEN SO THAT THIS CHANGE IS STRICTLY ADDITIVE.
// The pawn table is the one that was actually validated (v2.8.2, part of a
// +28.0 package). Its weight equals the divisor, so when only the pawn table
// has learned anything the correction is BIT-IDENTICAL to what v4.2.0
// produced, and the five new tables can only add on top of that, bounded.
//
// This matters more than it looks. If the new tables were folded in by plain
// averaging, an empty table would pull the correction toward zero and quietly
// shrink a validated behaviour by a factor of six - and then a failed SPRT
// would be unattributable between "the new keys are useless" and "we damaged
// the old one". Making the change additive keeps the measurement clean.
//
// The reference's own weighting (its key set - no major table, two
// continuation CONTEXT tables keyed by the move two and four plies up paired
// with the arriving move - and its relative read weights, rescaled to this
// set's 5/4 budget because the reference's weights act on gravity
// accumulators, not on residual means) was measured as the CorrectionBlend
// option on 2026-09-21 at 100,000 nodes against v5.9.11: flat, +1.6 +/- 9.6
// over 2,014 games. Its write-side rates are the same change in disguise on
// tables that store means, so the line was closed and the option, the two
// context tables and the per-node context keys removed the same day.
public sealed class CorrectionHistorySet
{
    // Pawn weight == divisor: pawn-only behaviour is unchanged from v4.2.0.
    private const int PawnWeight = 4;
    private const int OtherWeight = 1;
    private const int Divisor = PawnWeight;

    // The five secondary tables can together move the evaluation by at most
    // 5/4 of a full pawn-table correction, and the total is clamped regardless.
    private const int MaxTotalCorrectionCp = 320;

    private readonly CorrectionHistory _pawn = new();
    private readonly CorrectionHistory _minor = new();
    private readonly CorrectionHistory _major = new();
    // Indexed by colour: an asymmetric bias ("this evaluator misjudges White's
    // piece placement") is invisible to a colour-blind key.
    private readonly CorrectionHistory[] _nonPawn = [new(), new()];
    private readonly CorrectionHistory _continuation = new();

    public void Clear()
    {
        _pawn.Clear();
        _minor.Clear();
        _major.Clear();
        _nonPawn[0].Clear();
        _nonPawn[1].Clear();
        _continuation.Clear();
    }

    // Corrects a raw static evaluation. 'continuationKey' encodes the move that
    // led here (see ContinuationKey); pass 0 at the root or after a null move,
    // where there is no such move and the table has nothing to say.
    public int Correct(Board board, int rawEval, ulong continuationKey)
    {
        long weighted =
            (long)PawnWeight * _pawn.RawEntry(board, board.PawnZobristKey)
            + OtherWeight * _minor.RawEntry(board, board.MinorZobristKey)
            + OtherWeight * _major.RawEntry(board, board.MajorZobristKey)
            + OtherWeight * _nonPawn[0].RawEntry(board, board.NonPawnZobristKey(Color.White))
            + OtherWeight * _nonPawn[1].RawEntry(board, board.NonPawnZobristKey(Color.Black))
            + (continuationKey != 0
                ? OtherWeight * _continuation.RawEntry(board, continuationKey)
                : 0);

        // One rounding step at the end, over the combined numerator, instead of
        // one per table.
        int correction = (int)(weighted / ((long)Divisor * CorrectionHistory.Scale));
        return rawEval + Math.Clamp(correction, -MaxTotalCorrectionCp, MaxTotalCorrectionCp);
    }

    // Feeds the observed residual to every table. They all learn from the same
    // observation; what differs is the key each one files it under, which is
    // what lets them generalise over different things.
    // CorrectionGravity: every table integrates with a gravity rule instead of
    // tracking the post-correction residual (see CorrectionHistory.Update).
    public bool Gravity;
    // CorrectionWeightCap: the EMA's per-update weight capped at 64/256.
    public bool WeightCap;

    public void Update(Board board, int errorCp, int depth, ulong continuationKey)
    {
        _pawn.Update(board, board.PawnZobristKey, errorCp, depth, Gravity, WeightCap);
        _minor.Update(board, board.MinorZobristKey, errorCp, depth, Gravity, WeightCap);
        _nonPawn[0].Update(board, board.NonPawnZobristKey(Color.White), errorCp, depth, Gravity, WeightCap);
        _nonPawn[1].Update(board, board.NonPawnZobristKey(Color.Black), errorCp, depth, Gravity, WeightCap);
        _major.Update(board, board.MajorZobristKey, errorCp, depth, Gravity, WeightCap);
        if (continuationKey != 0)
            _continuation.Update(board, continuationKey, errorCp, depth, Gravity, WeightCap);
    }

    // Key for the continuation table: the (piece, destination) of the move that
    // reached this position. Unlike the other keys this describes HOW the
    // position was arrived at rather than what is on the board, which is a
    // genuinely different axis - the same position reached by a quiet
    // regrouping and by a forcing capture tends to be misjudged differently.
    //
    // +1 keeps the key non-zero, since 0 is the caller's sentinel for "no
    // previous move" (root, or immediately after a null move).
    public static ulong ContinuationKey(int pieceIndex, int toSquare)
        => (ulong)(pieceIndex * 64 + toSquare) + 1;
}
