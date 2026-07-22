using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Continuation history: like the classic history heuristic, but conditioned on
// the PREVIOUS move. Indexed by (previous mover piece, previous destination,
// current mover piece, current destination), it learns "after the opponent
// plays THIS, THAT reply keeps refuting" — a much sharper signal than the
// global (from, to) butterfly history, because the best reply to h7-h5 is
// rarely the best reply to d7-d5 even from the same square.
//
// Pieces are indexed 0-11 (color * 6 + type) so the table distinguishes a
// white knight from a black one. The full table is 12*64*12*64 ints (~2.3 MB),
// small enough to keep hot in cache for the entries that actually occur.
public sealed class ContinuationHistory
{
    // Gravity updates keep entries bounded without sweeping this multi-megabyte
    // table or pinning frequent continuations permanently to its rails.
    private const int MaxScore = 1 << 20;

    private readonly int[] _scores = new int[12 * 64 * 12 * 64];

    public static int PieceIndex(Color color, PieceType type) => (int)color * 6 + (int)type;

    private static int Index(int prevPiece, int prevTo, int piece, int to)
        => ((prevPiece * 64 + prevTo) * 12 + piece) * 64 + to;

    public void Clear() => Array.Clear(_scores);

    public int Get(int prevPiece, int prevTo, int piece, int to)
        => _scores[Index(prevPiece, prevTo, piece, to)];

    // Rewards the quiet reply that caused a beta cutoff after 'prev' was played.
    public void AddBonus(int prevPiece, int prevTo, int piece, int to, int depth)
        => Update(prevPiece, prevTo, piece, to, depth * depth);

    // Punishes quiet replies that were searched before the cutoff move and
    // failed to produce it — they sink in the ordering next time.
    public void AddMalus(int prevPiece, int prevTo, int piece, int to, int depth)
        => Update(prevPiece, prevTo, piece, to, -depth * depth);

    private void Update(int prevPiece, int prevTo, int piece, int to, int bonus)
    {
        bonus = Math.Clamp(bonus, -MaxScore, MaxScore);
        ref int score = ref _scores[Index(prevPiece, prevTo, piece, to)];
        score += bonus - (int)((long)score * Math.Abs(bonus) / MaxScore);
    }
}
