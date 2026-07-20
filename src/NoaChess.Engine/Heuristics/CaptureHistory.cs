using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Capture history: for every (mover piece, destination, captured piece type)
// it accumulates how often that capture turned out best. Where MVV-LVA ranks
// captures by static material logic alone, this table learns from actual
// search outcomes — QxP may be the biggest victim gain on the board and still
// be a known lemon in this structure. Introduced for ProbCut ordering (5F);
// the reference also feeds it into capture futility and the main capture
// ordering, which is a later block.
//
// Pieces are indexed 0-11 (color * 6 + type); the victim only needs its TYPE
// (0-5, plus slot 6 for "none" so non-capture queen promotions index safely).
public sealed class CaptureHistory
{
    // Asymptotic bound of the gravity update: entries approach but never
    // exceed +/-MaxScore, so no explicit clamp is needed. Sized so a strong
    // history signal (~65% of the bound) is comparable to the 7x victim-value
    // term it is added to in ProbCut ordering — the same ratio the reference
    // keeps between its bound (10692) and 7x its queen value.
    private const int MaxScore = 4096;

    private readonly int[] _scores = new int[12 * 64 * 7];

    private static int Index(int piece, int to, int capturedType)
        => (piece * 64 + to) * 7 + capturedType;

    // Victim type slot for a move: en passant captures a pawn on an empty
    // destination square; non-capture promotions have no victim (slot 6).
    public static int VictimIndex(Board board, Move move)
        => move.Flag == MoveFlag.EnPassant ? (int)PieceType.Pawn
         : move.IsCapture ? (int)board.PieceTypeAt(move.To)
         : 6;

    public void Clear() => Array.Clear(_scores);

    public int Get(int piece, int to, int capturedType)
        => _scores[Index(piece, to, capturedType)];

    // Gravity update (reference formula): entry += bonus - entry*|bonus|/Max.
    // The pull term grows with how far the entry already leans, so stale
    // accumulations yield to fresh evidence and values never park on a rail.
    public void AddBonus(int piece, int to, int capturedType, int bonus)
    {
        ref int score = ref _scores[Index(piece, to, capturedType)];
        score += bonus - score * bonus / MaxScore;
    }

    public void AddMalus(int piece, int to, int capturedType, int malus)
    {
        ref int score = ref _scores[Index(piece, to, capturedType)];
        score += -malus - score * malus / MaxScore;
    }
}
