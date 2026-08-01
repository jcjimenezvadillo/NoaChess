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

    private readonly int[,] _entries = new int[2, TableSize];

    public void Clear() => Array.Clear(_entries);

    // Raw (still scaled) entry for this position. The caller combines several
    // tables before dividing, so that rounding happens once at the end instead
    // of once per table.
    public int RawEntry(Board board, ulong key)
        => _entries[(int)board.SideToMove, (int)(key & (TableSize - 1))];

    public void Update(Board board, ulong key, int errorCp, int depth)
    {
        int target = Math.Clamp(errorCp, -MaxCorrectionCp, MaxCorrectionCp) * Scale;
        int weight = Math.Min(16 + depth * depth, 128);
        ref int entry = ref _entries[(int)board.SideToMove, (int)(key & (TableSize - 1))];

        // Bounded exponential update toward the observed residual. Deep results
        // are better teachers, while shallow noise changes the estimate slowly.
        entry += (int)(((long)target - entry) * weight / 256);
    }
}
