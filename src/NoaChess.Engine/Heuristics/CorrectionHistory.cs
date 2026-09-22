using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// One correction-history table: the learned residual between static evaluation
// and searched scores, indexed by (side to move, some structural key).
//
// WHY IT WORKS. A static evaluator has systematic biases, not just random ones -
// it misjudges particular structures the same way every time it meets them.
// Those structures recur across many branches of a search, so the difference
// between what the evaluator said and what the search actually found is worth
// remembering. Correcting the static evaluation before it feeds forward pruning
// and the improving flag removes that bias where it does the most damage.
//
// v4.3.0 generalises what was PawnCorrectionHistory. The pawn key was the only
// one available and it validated in v2.8.2, but pawn structure is not the only
// signal an evaluator can be systematically wrong about: a bias that follows
// the minor pieces recurs across positions whose pawns differ, and a bias in
// how one side's pieces are judged is invisible to a colour-blind key. One
// table per key, combined by CorrectionHistorySet.
public sealed class CorrectionHistory
{
    private const int TableSize = 1 << 14;

    // Entries are stored at Scale times the centipawn residual so the
    // exponential update below keeps sub-centipawn resolution in integers.
    public const int Scale = 64;
    private const int MaxCorrectionCp = 256;

    // FLAT, not int[2, TableSize]: read on every corrected static evaluation.
    // Layout: side * TableSize + slot.
    private readonly int[] _entries = new int[2 * TableSize];

    private static int Index(Board board, ulong key)
        => ((int)board.SideToMove * TableSize) + (int)(key & (TableSize - 1));

    public void Clear() => Array.Clear(_entries);

    // Raw (still scaled) entry for this position. The caller combines several
    // tables before dividing, so that rounding happens once at the end instead
    // of once per table.
    public int RawEntry(Board board, ulong key) => _entries[Index(board, key)];

    public void Update(Board board, ulong key, int errorCp, int depth, bool gravity = false,
                       bool weightCap = false)
    {
        ref int entry = ref _entries[Index(board, key)];
        if (gravity)
        {
            // CorrectionGravity (re-investigation of 2026-09-21). The update
            // below moves each table toward the POST-correction residual, so a
            // bias every table sees settles at W / (1 + W) = 69% corrected
            // (W = 2.25, the set's read gain), and one deep update can move a
            // structure by up to 288 cp. The reference integrates instead:
            // a gravity accumulator (the same val + b - val*|b|/D rule as the
            // history tables) whose equilibrium does not depend on the read
            // weights, so a clean bias is corrected almost fully and noisy
            // keys shrink. Loop gain W * depth / 37 = 0.061 * depth, the
            // reference's; at most 32 cp per table per update, 128 cp bound
            // per table, 288 cp in total under the set's 320 clamp.
            int e = Math.Clamp(errorCp, -MaxCorrectionCp, MaxCorrectionCp);
            int bonus = Math.Clamp(e * Scale * depth / 37, -2048, 2048);
            entry += bonus - (int)((long)entry * Math.Abs(bonus) / 8192);
            return;
        }

        int target = Math.Clamp(errorCp, -MaxCorrectionCp, MaxCorrectionCp) * Scale;
        // CorrectionWeightCap (third form, 2026-09-22, after the gravity rule
        // measured H0 at -9.6 over 1,082: correcting MORE cost Elo). The other
        // direction: read gain 2.25 times this weight is how far one update
        // moves the correction, 1.02 of the observed deviation at depth 10 and
        // 1.125 from depth 11, so one deep result can swing a structure by up
        // to 288 cp. Capped at 64/256 the product stays at or under 0.56.
        int weight = Math.Min(16 + depth * depth, weightCap ? 64 : 128);

        // Bounded exponential update toward the observed residual. Deep results
        // are better teachers, while shallow noise changes the estimate slowly.
        entry += (int)(((long)target - entry) * weight / 256);
    }
}
