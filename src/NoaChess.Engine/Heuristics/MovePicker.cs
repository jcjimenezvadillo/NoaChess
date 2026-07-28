using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Move ordering. Alpha-Beta prunes exponentially better when the best move is
// tried first (a perfectly ordered tree costs roughly the square root of an
// unordered one), so this ranking is one of the highest-impact parts of the
// engine. The v1.0 order:
//
//   1. TT move (proven best in a previous search of this very position).
//   2. Winning/equal captures (SEE >= 0), ranked by capture history plus
//      seven times the victim value.
//   3. Non-capture promotions.
//   4. Killer moves (quiet refutations from sibling nodes).
//   5. Counter move (the quiet refutation of the opponent's LAST move).
//   6. Remaining quiet moves, ranked by history + continuation history.
//   7. Losing captures (SEE < 0) — tried last: they are usually just blunders,
//      but occasionally a sacrifice, so they cannot be skipped entirely here.
public static class MovePicker
{
    // Score bands. History is explicitly clamped below the counter-move band;
    // preserving these refutation stages won the final v2.8.2 SPRT.
    private const int TTMoveScore = 10_000_000;
    private const int GoodCaptureBase = 5_000_000;
    private const int PromotionBase = 4_000_000;
    private const int KillerBase = 3_000_000;
    private const int CounterMoveScore = 2_900_000;
    private const int HistoryCap = CounterMoveScore - 10;
    private const int LosingCaptureBase = -5_000_000;
    private const int CheckBonus = 16_384;
    private const int ThreatEscapeWeight = 20;
    private const int QuietSortDepthFactor = 3_000;
    private const int CheckSeeThreshold = 75;

    // Rough piece values for capture ordering and threat-escape bonuses
    // (index = PieceType; a king never appears as a victim).
    private static readonly int[] PieceValue = [100, 320, 330, 500, 900, 20_000, 0];

    // Enemy attacks by progressively more valuable piece groups, built once
    // per scored quiet batch. A rook is threatened by pawns/minors; a queen
    // additionally by rooks. Kings and pawns have no "lesser piece" signal.
    private readonly record struct QuietOrderingContext(
        ulong PawnThreats, ulong MinorThreats, ulong RookThreats)
    {
        public ulong ThreatsFor(PieceType type) => type switch
        {
            PieceType.Knight or PieceType.Bishop => PawnThreats,
            PieceType.Rook => PawnThreats | MinorThreats,
            PieceType.Queen => PawnThreats | MinorThreats | RookThreats,
            _ => 0,
        };
    }

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
                             Move counterMove, CaptureHistory? captureHistory = null)
    {
        int n = moves.Count;
        if (n < 2)
            return;

        int[] scores = moves.Scores;
        QuietOrderingContext quietContext = BuildQuietOrderingContext(board);
        for (int i = 0; i < n; i++)
            scores[i] = Score(moves[i], board, ttMove, killers, history, ply,
                              contHist, prevPiece, prevTo, counterMove, captureHistory,
                              quietContext);

        SortRange(moves, 0);
    }

    // Capture-only variant used by quiescence search (no killers/history: only
    // captures are searched there).
    public static void OrderCaptures(MoveList moves, Board board,
                                     CaptureHistory? captureHistory = null) =>
        Order(moves, board, Move.None, NoKillers, NoHistory, 0,
              contHist: null, prevPiece: -1, prevTo: 0, counterMove: Move.None,
              captureHistory: captureHistory);

    // ---------- Staged-picker range helpers ----------
    // The staged loop in AlphaBetaSearch appends captures and quiets to the
    // same list in phases; each phase scores and sorts only its own tail so
    // moves already served keep their positions.

    // Scores moves[from..Count) as captures/promotions and sorts the range.
    // Winning/equal captures land above 0, losing captures in a deeply
    // negative band — the caller uses the sign as the "losers start here" cue.
    public static void ScoreAndSortCaptures(MoveList moves, int from, Board board,
                                            CaptureHistory? captureHistory = null)
    {
        int[] scores = moves.Scores;
        for (int i = from; i < moves.Count; i++)
            scores[i] = Score(moves[i], board, Move.None, NoKillers, NoHistory, 0,
                              contHist: null, prevPiece: -1, prevTo: 0,
                              counterMove: Move.None, captureHistory: captureHistory,
                              quietContext: default);
        SortRange(moves, from);
    }

    // Scores moves[quietsFrom..Count) as quiets (killers, counter move,
    // history), then sorts moves[sortFrom..Count). sortFrom may sit earlier
    // than quietsFrom so unserved losing captures merge into the same order —
    // their band is far below any quiet score, so they sink to the very end.
    public static void ScoreAndSortQuiets(MoveList moves, int quietsFrom, int sortFrom,
                                          Board board, KillerTable killers, HistoryTable history,
                                          int ply, ContinuationHistory? contHist,
                                          int prevPiece, int prevTo, Move counterMove,
                                          int? depth = null)
    {
        int[] scores = moves.Scores;
        QuietOrderingContext quietContext = BuildQuietOrderingContext(board);
        for (int i = quietsFrom; i < moves.Count; i++)
            scores[i] = Score(moves[i], board, Move.None, killers, history, ply,
                              contHist, prevPiece, prevTo, counterMove,
                              captureHistory: null, quietContext: quietContext);

        if (depth is int searchDepth)
        {
            // The staged picker still has its losing captures in
            // [sortFrom, quietsFrom). Reference stages serve every quiet move
            // before those captures, including quiets below the partial-sort
            // threshold, so first move the entire quiet block in front.
            int quietCount = moves.Count - quietsFrom;
            MoveRangeToFront(moves, quietsFrom, sortFrom, quietCount);
            PartialSortRange(moves, sortFrom, sortFrom + quietCount,
                             QuietSortLimit(searchDepth));
        }
        else
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
            scores[i] = CaptureOrderingScore(move, board, us, captureHistory);
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

    // Sorts only scores at or above 'limit' into a descending prefix. The
    // low-scored tail is still searched, but paying O(n^2) to order it has
    // little value because most nodes cut before reaching those moves.
    private static void PartialSortRange(MoveList moves, int from, int to, int limit)
    {
        int[] scores = moves.Scores;
        int sortedEnd = from - 1;

        for (int i = from; i < to; i++)
        {
            int score = scores[i];
            if (score < limit)
                continue;

            Move move = moves[i];
            sortedEnd++;
            if (i != sortedEnd)
            {
                moves[i] = moves[sortedEnd];
                scores[i] = scores[sortedEnd];
            }

            int j = sortedEnd - 1;
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

    // Moves the contiguous range [source, source + count) to destination by
    // swapping it over the preceding range. This is the staged picker''s
    // allocation-free equivalent of placing QUIET moves before BAD_CAPTURE.
    private static void MoveRangeToFront(MoveList moves, int source,
                                         int destination, int count)
    {
        int[] scores = moves.Scores;
        for (int offset = 0; offset < count && source != destination; offset++)
        {
            int from = source + offset;
            int to = destination + offset;
            (moves[to], moves[from]) = (moves[from], moves[to]);
            (scores[to], scores[from]) = (scores[from], scores[to]);
        }
    }

    private static int QuietSortLimit(int depth)
    {
        long limit = -(long)QuietSortDepthFactor * Math.Max(depth, 0);
        return (int)Math.Max(limit, int.MinValue);
    }

    // Empty shared instances so OrderCaptures can reuse the same scorer.
    private static readonly KillerTable NoKillers = new(1);
    private static readonly HistoryTable NoHistory = new();

    private static int Score(Move move, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply,
                             ContinuationHistory? contHist, int prevPiece, int prevTo,
                             Move counterMove, CaptureHistory? captureHistory = null,
                             QuietOrderingContext quietContext = default)
    {
        if (move == ttMove)
            return TTMoveScore;

        if (move.IsCapture)
        {
            int captureScore = CaptureOrderingScore(
                move, board, board.SideToMove, captureHistory);

            // SEE decides the band: winning/equal exchanges up front, losing
            // ones at the very back. Inside either band, seven times the
            // victim value supplies the material prior and capture history
            // learns which exchanges actually work in searched positions.
            return StaticExchangeEvaluator.LosesAtLeast(board, move)
                ? LosingCaptureBase + captureScore
                : GoodCaptureBase + captureScore;
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

        PieceType mover = board.PieceTypeAt(move.From);
        if (GivesDirectCheck(board, move, mover)
            && !StaticExchangeEvaluator.LosesAtLeast(board, move, CheckSeeThreshold))
            quietScore += CheckBonus;

        ulong lesserThreats = quietContext.ThreatsFor(mover);
        int escapesThreat = Bitboard.IsSet(lesserThreats, move.From) ? 1 : 0;
        int entersThreat = Bitboard.IsSet(lesserThreats, move.To) ? 1 : 0;
        quietScore += PieceValue[(int)mover] * ThreatEscapeWeight
                    * (escapesThreat - entersThreat);

        return Math.Min(quietScore, HistoryCap);
    }

    private static QuietOrderingContext BuildQuietOrderingContext(Board board)
    {
        Color them = Board.OppositeColor(board.SideToMove);
        ulong occ = board.AllOccupancy;
        ulong pawns = board.Pieces(them, PieceType.Pawn);
        ulong pawnThreats = them == Color.White
            ? ((pawns & ~Bitboard.FileA) << 7) | ((pawns & ~Bitboard.FileH) << 9)
            : ((pawns & ~Bitboard.FileA) >> 9) | ((pawns & ~Bitboard.FileH) >> 7);

        ulong minorThreats = 0;
        ulong pieces = board.Pieces(them, PieceType.Knight);
        while (pieces != 0)
            minorThreats |= Attacks.Knight(Bitboard.PopLsb(ref pieces));
        pieces = board.Pieces(them, PieceType.Bishop);
        while (pieces != 0)
            minorThreats |= Attacks.Bishop(Bitboard.PopLsb(ref pieces), occ);

        ulong rookThreats = 0;
        pieces = board.Pieces(them, PieceType.Rook);
        while (pieces != 0)
            rookThreats |= Attacks.Rook(Bitboard.PopLsb(ref pieces), occ);

        return new QuietOrderingContext(pawnThreats, minorThreats, rookThreats);
    }

    // The reference move picker rewards direct checks from the moved piece;
    // discovered checks are left to the full search's gives-check test.
    private static bool GivesDirectCheck(Board board, Move move, PieceType mover)
    {
        Color us = board.SideToMove;
        int to = move.To;
        ulong king = Bitboard.SquareBB(board.KingSquare(Board.OppositeColor(us)));
        ulong occ = (board.AllOccupancy & ~Bitboard.SquareBB(move.From))
                  | Bitboard.SquareBB(to);

        ulong attacks = mover switch
        {
            PieceType.Pawn => Attacks.Pawn(us, to),
            PieceType.Knight => Attacks.Knight(to),
            PieceType.Bishop => Attacks.Bishop(to, occ),
            PieceType.Rook => Attacks.Rook(to, occ),
            PieceType.Queen => Attacks.Queen(to, occ),
            PieceType.King => Attacks.King(to),
            _ => 0,
        };
        return (attacks & king) != 0;
    }

    // Reference capture ordering shared by the main and quiescence pickers.
    // Capture promotions also include the promoted piece value so the queen
    // precedes underpromotions when victim and learned history are identical.
    private static int CaptureOrderingScore(Move move, Board board, Color us,
                                            CaptureHistory? captureHistory)
    {
        PieceType victim = move.Flag == MoveFlag.EnPassant
            ? PieceType.Pawn
            : board.PieceTypeAt(move.To);
        int score = 7 * PieceValue[(int)victim];

        if (captureHistory is not null)
        {
            int piece = ContinuationHistory.PieceIndex(us, board.PieceTypeAt(move.From));
            score += captureHistory.Get(
                piece, move.To, CaptureHistory.VictimIndex(board, move));
        }

        if (move.IsPromotion)
            score += PieceValue[(int)move.PromotionPiece];

        return score;
    }
}
