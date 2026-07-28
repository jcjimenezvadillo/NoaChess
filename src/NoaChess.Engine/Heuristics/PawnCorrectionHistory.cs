using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Learns the residual between static evaluation and searched scores for each
// pawn structure and side to move. Pawn structures recur across many branches,
// so this inexpensive signal corrects systematic evaluator bias before forward
// pruning and improving decisions consume the static evaluation.
public sealed class PawnCorrectionHistory
{
    private const int TableSize = 1 << 14;
    private const int Scale = 64;
    private const int MaxCorrectionCp = 256;

    private readonly int[,] _entries = new int[2, TableSize];

    public void Clear() => Array.Clear(_entries);

    public int Correct(Board board, int rawEval)
        => rawEval + _entries[(int)board.SideToMove, Index(board)] / Scale;

    public void Update(Board board, int errorCp, int depth)
    {
        int target = Math.Clamp(errorCp, -MaxCorrectionCp, MaxCorrectionCp) * Scale;
        int weight = Math.Min(16 + depth * depth, 128);
        ref int entry = ref _entries[(int)board.SideToMove, Index(board)];

        // Bounded exponential update toward the observed residual. Deep results
        // are better teachers, while shallow noise changes the estimate slowly.
        entry += (int)(((long)target - entry) * weight / 256);
    }

    private static int Index(Board board) => (int)(board.PawnZobristKey & (TableSize - 1));
}
