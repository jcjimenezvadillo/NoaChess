using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Move ordering. Alpha-Beta prunes exponentially better when the best move is
// tried first (a perfectly ordered tree costs roughly the square root of an
// unordered one), so this ranking is one of the highest-impact parts of the
// engine. The v1.0 order:
//
//   1. TT move (proven best in a previous search of this very position).
//   2. Winning/equal captures (SEE >= 0), ranked by MVV-LVA.
//   3. Non-capture promotions.
//   4. Killer moves (quiet refutations from sibling nodes).
//   5. Counter move (the quiet refutation of the opponent's LAST move).
//   6. Remaining quiet moves, ranked by history + continuation history.
//   7. Losing captures (SEE < 0) — tried last: they are usually just blunders,
//      but occasionally a sacrifice, so they cannot be skipped entirely here.
public static class MovePicker
{
    // Score bands. They only need to keep the categories apart; the exact
    // numbers are irrelevant as long as the bands never overlap. History is
    // explicitly clamped below the counter-move band to guarantee that; its
    // floor (two maluses of -2^20) stays above the losing-capture band.
    private const int TTMoveScore = 10_000_000;
    private const int GoodCaptureBase = 5_000_000;
    private const int PromotionBase = 4_000_000;
    private const int KillerBase = 3_000_000;
    private const int CounterMoveScore = 2_900_000;
    private const int HistoryCap = CounterMoveScore - 10;
    private const int LosingCaptureBase = -5_000_000;

    // Rough piece values for MVV-LVA (index = PieceType, king included as
    // attacker only — a king "capture" never appears as a victim).
    private static readonly int[] PieceValue = [100, 320, 330, 500, 900, 20_000, 0];

    // Sorts 'moves' in place, best candidates first. Allocation-free: the
    // scores live in the MoveList's parallel array and an insertion sort keeps
    // them together with the moves (n is small — typically 20-45 — so
    // insertion sort beats fancier algorithms here).
    public static void Order(MoveList moves, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply) =>
        Order(moves, board, ttMove, killers, history, ply,
              contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None);

    // Full-context variant: also ranks the counter move to the opponent's last
    // move and blends continuation history into the quiet-move scores.
    // prevPiece < 0 means "no usable previous move" (root or after a null move).
    public static void Order(MoveList moves, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply,
                             ContinuationHistory? contHist, int prevPiece, int prevTo,
                             Move counterMove)
    {
        int n = moves.Count;
        if (n < 2)
            return;

        int[] scores = moves.Scores;
        for (int i = 0; i < n; i++)
            scores[i] = Score(moves[i], board, ttMove, killers, history, ply,
                              contHist, prevPiece, prevTo, counterMove);

        for (int i = 1; i < n; i++)
        {
            Move move = moves[i];
            int score = scores[i];
            int j = i - 1;
            while (j >= 0 && scores[j] < score)
            {
                moves[j + 1] = moves[j];
                scores[j + 1] = scores[j];
                j--;
            }
            moves[j + 1] = move;
            scores[j + 1] = score;
        }
    }

    // Capture-only variant used by quiescence search (no killers/history: only
    // captures are searched there).
    public static void OrderCaptures(MoveList moves, Board board) =>
        Order(moves, board, Move.None, NoKillers, NoHistory, 0);

    // ---------- Staged-picker range helpers ----------
    // The staged loop in AlphaBetaSearch appends captures and quiets to the
    // same list in phases; each phase scores and sorts only its own tail so
    // moves already served keep their positions.

    // Scores moves[from..Count) as captures/promotions and sorts the range.
    // Winning/equal captures land above 0, losing captures in a deeply
    // negative band — the caller uses the sign as the "losers start here" cue.
    public static void ScoreAndSortCaptures(MoveList moves, int from, Board board)
    {
        int[] scores = moves.Scores;
        for (int i = from; i < moves.Count; i++)
            scores[i] = Score(moves[i], board, Move.None, NoKillers, NoHistory, 0,
                              contHist: null, prevPiece: -1, prevTo: 0, Move.None);
        SortRange(moves, from);
    }

    // Scores moves[quietsFrom..Count) as quiets (killers, counter move,
    // history), then sorts moves[sortFrom..Count). sortFrom may sit earlier
    // than quietsFrom so unserved losing captures merge into the same order —
    // their band is far below any quiet score, so they sink to the very end.
    public static void ScoreAndSortQuiets(MoveList moves, int quietsFrom, int sortFrom,
                                          Board board, KillerTable killers, HistoryTable history,
                                          int ply, ContinuationHistory? contHist,
                                          int prevPiece, int prevTo, Move counterMove)
    {
        int[] scores = moves.Scores;
        for (int i = quietsFrom; i < moves.Count; i++)
            scores[i] = Score(moves[i], board, Move.None, killers, history, ply,
                              contHist, prevPiece, prevTo, counterMove);
        SortRange(moves, sortFrom);
    }

    // Quiescence capture ordering (reference movepick QCAPTURE stage): learned
    // exchange outcomes plus 7x the victim's value. Where plain MVV-LVA calls
    // two captures of the same piece equal, capture history knows which of
    // them has actually been working. Promotions keep their own band so the
    // queen promotion still leads and the minors trail the captures.
    public static void ScoreAndSortCapturesQs(MoveList moves, Board board,
                                              CaptureHistory captureHistory)
    {
        int[] scores = moves.Scores;
        Color us = board.SideToMove;
        for (int i = 0; i < moves.Count; i++)
        {
            Move move = moves[i];
            if (move.IsPromotion && !move.IsCapture)
            {
                scores[i] = PromotionBase + PieceValue[(int)move.PromotionPiece];
                continue;
            }
            PieceType victim = move.Flag == MoveFlag.EnPassant
                ? PieceType.Pawn
                : board.PieceTypeAt(move.To);
            int piece = ContinuationHistory.PieceIndex(us, board.PieceTypeAt(move.From));
            scores[i] = captureHistory.Get(piece, move.To, CaptureHistory.VictimIndex(board, move))
                      + 7 * PieceValue[(int)victim];
        }
        SortRange(moves, 0);
    }

    // In-place insertion sort of moves[from..Count) by descending score.
    private static void SortRange(MoveList moves, int from)
    {
        int[] scores = moves.Scores;
        for (int i = from + 1; i < moves.Count; i++)
        {
            Move move = moves[i];
            int score = scores[i];
            int j = i - 1;
            while (j >= from && scores[j] < score)
            {
                moves[j + 1] = moves[j];
                scores[j + 1] = scores[j];
                j--;
            }
            moves[j + 1] = move;
            scores[j + 1] = score;
        }
    }

    // Empty shared instances so OrderCaptures can reuse the same scorer.
    private static readonly KillerTable NoKillers = new(1);
    private static readonly HistoryTable NoHistory = new();

    private static int Score(Move move, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply,
                             ContinuationHistory? contHist, int prevPiece, int prevTo,
                             Move counterMove)
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

            // MVV dominates (x10), LVA breaks ties within the band.
            int mvvLva = PieceValue[(int)victim] * 10 - PieceValue[(int)attacker];

            // SEE decides the band: winning/equal exchanges up front, losing
            // ones at the very back (LosesAtLeast short-circuits the full
            // exchange computation for downward/equal captures).
            return StaticExchangeEvaluator.LosesAtLeast(board, move)
                ? LosingCaptureBase + mvvLva
                : GoodCaptureBase + mvvLva;
        }

        if (move.IsPromotion)
            return PromotionBase + PieceValue[(int)move.PromotionPiece];

        int killerRank = killers.Rank(ply, move);
        if (killerRank > 0)
            return KillerBase + killerRank;

        if (move == counterMove)
            return CounterMoveScore;

        int quietScore = history.Get(board.SideToMove, move);
        if (contHist is not null && prevPiece >= 0)
        {
            int piece = ContinuationHistory.PieceIndex(board.SideToMove, board.PieceTypeAt(move.From));
            quietScore += contHist.Get(prevPiece, prevTo, piece, move.To);
        }
        return Math.Min(quietScore, HistoryCap);
    }
}
