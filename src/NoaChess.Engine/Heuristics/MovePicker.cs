using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Move ordering. Alpha-Beta prunes exponentially better when the best move is
// tried first (a perfectly ordered tree costs roughly the square root of an
// unordered one), so this ranking is one of the highest-impact parts of the
// engine. The order implemented here is the classic v0.2 recipe:
//
//   1. TT move (proven best in a previous search of this very position).
//   2. Captures, ranked by MVV-LVA.
//   3. Killer moves (quiet refutations from sibling nodes).
//   4. Remaining quiet moves, ranked by history score.
//
// MVV-LVA = "Most Valuable Victim, Least Valuable Attacker": capturing a queen
// with a pawn is examined before capturing a pawn with a queen. It is a crude
// but very effective stand-in for real static exchange evaluation (SEE, v1.0).
public static class MovePicker
{
    // Score bands. They only need to keep the categories apart; the exact
    // numbers are irrelevant as long as the bands never overlap.
    private const int TTMoveScore = 1_000_000;
    private const int CaptureBase = 100_000;
    private const int KillerBase = 90_000;

    // Rough piece values for MVV-LVA (index = PieceType, king included as
    // attacker only — a king "capture" never appears as a victim).
    private static readonly int[] PieceValue = [100, 320, 330, 500, 900, 20_000, 0];

    // Sorts 'moves' in place, best candidates first.
    public static void Order(List<Move> moves, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply)
    {
        int n = moves.Count;
        if (n < 2)
            return;

        // Scores are negated so that Array.Sort's ascending order yields a
        // best-first move list.
        var keys = new int[n];
        var items = new Move[n];
        for (int i = 0; i < n; i++)
        {
            items[i] = moves[i];
            keys[i] = -Score(items[i], board, ttMove, killers, history, ply);
        }

        Array.Sort(keys, items);

        for (int i = 0; i < n; i++)
            moves[i] = items[i];
    }

    // Capture-only variant used by quiescence search (no killers/history: only
    // captures are searched there and MVV-LVA is what matters).
    public static void OrderCaptures(List<Move> moves, Board board) =>
        Order(moves, board, Move.None, NoKillers, NoHistory, 0);

    // Empty shared instances so OrderCaptures can reuse the same scorer.
    private static readonly KillerTable NoKillers = new(1);
    private static readonly HistoryTable NoHistory = new();

    private static int Score(Move move, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply)
    {
        if (move == ttMove)
            return TTMoveScore;

        if (move.IsCapture)
        {
            // In an en passant capture the destination square is empty: the
            // victim is always a pawn.
            PieceType victim = move.Flag == MoveFlag.EnPassant
                ? PieceType.Pawn
                : board.PieceTypeAt(move.To);
            PieceType attacker = board.PieceTypeAt(move.From);

            // MVV dominates (x10), LVA breaks ties.
            return CaptureBase + PieceValue[(int)victim] * 10 - PieceValue[(int)attacker];
        }

        // Non-capture promotions are nearly as strong as captures.
        if (move.IsPromotion)
            return CaptureBase + PieceValue[(int)move.PromotionPiece];

        int killerRank = killers.Rank(ply, move);
        if (killerRank > 0)
            return KillerBase + killerRank;

        return history.Get(board.SideToMove, move);
    }
}
