using NoaChess.Core;

namespace NoaChess.Engine.Heuristics;

// Move ordering. Alpha-Beta prunes exponentially better when the best move is
// tried first (a perfectly ordered tree costs roughly the square root of an
// unordered one), so this ranking is one of the highest-impact parts of the
// engine. The order:
//
//   1. TT move (proven best in a previous search of this very position).
//   2. Winning/equal captures (SEE >= 0), ranked by capture history plus
//      seven times the victim value.
//   3. Non-capture promotions.
//   4. Quiet moves, ranked in one additive history space: butterfly history,
//      continuation history, killer and counter-move bonuses, a safe-check
//      bonus and a threat-escape term.
//   5. Losing captures (SEE < 0) - tried last: they are usually just blunders,
//      but occasionally a sacrifice, so they cannot be skipped entirely here.
public static class MovePicker
{
    // Move-class bands. Only the classes that are genuinely ordered by KIND
    // (transposition move, captures by SEE sign, promotions) get a band;
    // everything quiet competes inside a single additive score.
    private const int TTMoveScore = 10_000_000;
    private const int GoodCaptureBase = 5_000_000;
    private const int PromotionBase = 4_000_000;
    private const int LosingCaptureBase = -5_000_000;
    private const int CheckBonus = 16_384;
    private const int ThreatEscapeWeight = 20;
    private const int QuietSortDepthFactor = 3_000;
    private const int CheckSeeThreshold = 75;

    // Killer and counter-move bonuses.
    //
    // These used to be absolute bands (killers at 3_000_000, counter move at
    // 2_900_000) returned BEFORE history was even read, with every
    // history-scored quiet clamped just underneath at 2_899_990. Up to three
    // moves per node were therefore ordered by a constant, and no amount of
    // learned evidence about THIS position could overtake a refutation
    // inherited from a sibling node. That put a hard ceiling on every
    // history-space experiment the engine has run: multi-level continuation
    // history was measured four times (-33.9, -10.9, ~0, -4.2) and a
    // butterfly-history LMR term three times, all rejected, because making
    // history smarter could only ever reorder what sat BELOW the reserved
    // slots.
    //
    // The bands did win the final v2.8.2 SPRT against removing them, but that
    // measurement is not evidence for keeping them today: at v2.8.2 the
    // butterfly table was numerically broken (2^20 rail, gravity term
    // integer-truncating to zero, median -8 against a mean of +71.8, only 25%
    // of entries positive). Ordering by a table in that state would lose to
    // almost any fixed prior. The rails were rebuilt afterwards (7183 here,
    // 8192 for continuation history), so the comparison is worth making again
    // on a table that actually works.
    //
    // Sized against the learned evidence a quiet can actually carry: butterfly
    // history reaches 7183 and continuation history 8192, so about 15k total.
    // A killer therefore starts roughly a quarter of that ahead - a strong
    // prior, but one that genuinely good local evidence can now pass.
    //
    // These magnitudes are NOT bench-derived, and deliberately so. A paired
    // 150-position node bench put 0, 4096, 8192 and 16384 all within +-2%
    // geometric mean of each other with every 95% band crossing zero (sign
    // test p = 0.93, 0.93, 0.46, 0.16) and single positions swinging between
    // x0.29 and x3.86. Node counts can say that none of these wrecks the
    // ordering; they cannot rank them. Only games can, so the value is set on
    // the argument above and left for SPRT to confirm.
    //
    // The reference carries no such prior at all - it has no killer or
    // counter-move stage, only history space. Going to zero here is the
    // eventual target, but its quiet score also sums FIVE continuation-history
    // levels plus pawn history, where this engine currently sums one. The
    // prior stays until that side is built up.
    private const int KillerBonus = 4_096;
    private const int SecondKillerBonus = 3_072;
    private const int CounterMoveBonus = 2_048;

    // Rough piece values for capture ordering and threat-escape bonuses
    // (index = PieceType; a king never appears as a victim).
    private static readonly int[] PieceValue = [100, 320, 330, 500, 900, 20_000, 0];

    // Enemy attacks by progressively more valuable piece groups, built once
    // per scored quiet batch. A rook is threatened by pawns/minors; a queen
    // additionally by rooks. Kings and pawns have no "lesser piece" signal.
    // Also carries the direct-check masks. Checking "does this move give
    // check" per move used to generate a full occupancy-aware attack set for
    // EVERY quiet, which is the single most expensive thing in the scorer.
    // Both halves below are exact, not approximations:
    //
    //   - Knight/pawn/king attack relations are symmetric and
    //     occupancy-independent, so "our piece on 'to' checks their king" is
    //     just "'to' is in the set of squares attacking the king". Exact, one
    //     bitboard test, no generation at all.
    //   - Sliders do depend on occupancy, so they keep the exact per-move
    //     computation - but only after a cheap empty-board line test rejects
    //     the destinations that are not even aligned with the king, which is
    //     most of them. A slider can only check from the king's rays, so the
    //     empty-board rays are a strict superset and the filter cannot drop a
    //     real check.
    private readonly record struct QuietOrderingContext(
        ulong PawnThreats, ulong MinorThreats, ulong RookThreats,
        ulong PawnChecks, ulong KnightChecks, ulong KingChecks,
        ulong RookLines, ulong BishopLines)
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
    // them together with the moves (n is small - typically 20-45 - so
    // insertion sort beats fancier algorithms here).
    public static void Order(MoveList moves, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply) =>
        Order(moves, board, ttMove, killers, history, ply,
              contHist: default, counterMove: Move.None);

    // Full-context variant: also ranks the counter move to the opponent's last
    // move and blends continuation history into the quiet-move scores. Pass
    // 'default' for contHist where no previous move is usable (root, or a
    // capture-only scoring pass that never reaches the quiet path).
    public static void Order(MoveList moves, Board board, Move ttMove,
                             KillerTable killers, HistoryTable history, int ply,
                             in ContinuationContext contHist,
                             Move counterMove, CaptureHistory? captureHistory = null)
    {
        int n = moves.Count;
        if (n < 2)
            return;

        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        QuietOrderingContext quietContext = BuildQuietOrderingContext(board);
        for (int i = 0; i < n; i++)
            scores[i] = Score(items[i], board, ttMove, killers, history, ply,
                              contHist, counterMove, captureHistory,
                              quietContext);

        SortRange(moves, 0);
    }

    // Capture-only variant used by quiescence search (no killers/history: only
    // captures are searched there).
    public static void OrderCaptures(MoveList moves, Board board,
                                     CaptureHistory? captureHistory = null) =>
        Order(moves, board, Move.None, NoKillers, NoHistory, 0,
              contHist: default, counterMove: Move.None,
              captureHistory: captureHistory);

    // ---------- Staged-picker range helpers ----------
    // The staged loop in AlphaBetaSearch appends captures and quiets to the
    // same list in phases; each phase scores and sorts only its own tail so
    // moves already served keep their positions.

    // Scores moves[from..Count) as captures/promotions and sorts the range.
    // Winning/equal captures land above 0, losing captures in a deeply
    // negative band - the caller uses the sign as the "losers start here" cue.
    public static void ScoreAndSortCaptures(MoveList moves, int from, Board board,
                                            CaptureHistory? captureHistory = null)
    {
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        int count = moves.Count;
        for (int i = from; i < count; i++)
            scores[i] = Score(items[i], board, Move.None, NoKillers, NoHistory, 0,
                              contHist: default, counterMove: Move.None, captureHistory: captureHistory,
                              quietContext: default);
        SortRange(moves, from);
    }

    // Scores moves[quietsFrom..Count) as quiets (killers, counter move,
    // history), then sorts moves[sortFrom..Count). sortFrom may sit earlier
    // than quietsFrom so unserved losing captures merge into the same order -
    // their band is far below any quiet score, so they sink to the very end.
    public static void ScoreAndSortQuiets(MoveList moves, int quietsFrom, int sortFrom,
                                          Board board, KillerTable killers, HistoryTable history,
                                          int ply, in ContinuationContext contHist,
                                          Move counterMove,
                                          int? depth = null)
    {
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        int count = moves.Count;
        QuietOrderingContext quietContext = BuildQuietOrderingContext(board);
        for (int i = quietsFrom; i < count; i++)
            scores[i] = Score(items[i], board, Move.None, killers, history, ply,
                              contHist, counterMove,
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
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        int count = moves.Count;
        Color us = board.SideToMove;
        for (int i = 0; i < count; i++)
        {
            Move move = items[i];
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
        // Both arrays raw and the count in a local: the inner loop shuffles two
        // parallel arrays, so every element it touches through the indexer is a
        // property call plus a bounds check the JIT will not hoist.
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        int count = moves.Count;

        for (int i = from + 1; i < count; i++)
        {
            Move move = items[i];
            int score = scores[i];
            int j = i - 1;
            while (j >= from && scores[j] < score)
            {
                items[j + 1] = items[j];
                scores[j + 1] = scores[j];
                j--;
            }
            items[j + 1] = move;
            scores[j + 1] = score;
        }
    }

    // Sorts only scores at or above 'limit' into a descending prefix. The
    // low-scored tail is still searched, but paying O(n^2) to order it has
    // little value because most nodes cut before reaching those moves.
    private static void PartialSortRange(MoveList moves, int from, int to, int limit)
    {
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        int sortedEnd = from - 1;

        for (int i = from; i < to; i++)
        {
            int score = scores[i];
            if (score < limit)
                continue;

            Move move = items[i];
            sortedEnd++;
            if (i != sortedEnd)
            {
                items[i] = items[sortedEnd];
                scores[i] = scores[sortedEnd];
            }

            int j = sortedEnd - 1;
            while (j >= from && scores[j] < score)
            {
                items[j + 1] = items[j];
                scores[j + 1] = scores[j];
                j--;
            }
            items[j + 1] = move;
            scores[j + 1] = score;
        }
    }

    // Moves the contiguous range [source, source + count) to destination by
    // swapping it over the preceding range. This is the staged picker''s
    // allocation-free equivalent of placing QUIET moves before BAD_CAPTURE.
    private static void MoveRangeToFront(MoveList moves, int source,
                                         int destination, int count)
    {
        Move[] items = moves.Moves;
        int[] scores = moves.Scores;
        for (int offset = 0; offset < count && source != destination; offset++)
        {
            int from = source + offset;
            int to = destination + offset;
            (items[to], items[from]) = (items[from], items[to]);
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
                             in ContinuationContext contHist,
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

        // Every quiet move is scored in the SAME additive space: learned
        // history first, then the refutation priors, then the two positional
        // signals. A killer or counter move accumulates its own history like
        // any other quiet, so a refutation that keeps working here compounds
        // its bonus instead of being pinned to a fixed rank.
        PieceType mover = board.PieceTypeAt(move.From);
        int quietScore = history.Get(board.SideToMove, move);
        if (contHist.IsActive)
        {
            int piece = ContinuationHistory.PieceIndex(board.SideToMove, mover);
            quietScore += contHist.Sum(piece, move.To);
        }

        // Rank 2 is the most recent killer, rank 1 the older one.
        int killerRank = killers.Rank(ply, move);
        if (killerRank == 2)
            quietScore += KillerBonus;
        else if (killerRank == 1)
            quietScore += SecondKillerBonus;

        // Not exclusive with the killer bonus: a move that is both the killer
        // at this ply AND the refutation of the opponent's last move carries
        // two independent pieces of evidence, so it should outrank a move
        // carrying only one.
        if (move == counterMove)
            quietScore += CounterMoveBonus;

        if (GivesDirectCheck(board, move, mover, quietContext)
            && !StaticExchangeEvaluator.LosesAtLeast(board, move, CheckSeeThreshold))
            quietScore += CheckBonus;

        ulong lesserThreats = quietContext.ThreatsFor(mover);
        int escapesThreat = Bitboard.IsSet(lesserThreats, move.From) ? 1 : 0;
        int entersThreat = Bitboard.IsSet(lesserThreats, move.To) ? 1 : 0;
        quietScore += PieceValue[(int)mover] * ThreatEscapeWeight
                    * (escapesThreat - entersThreat);

        return quietScore;
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

        // Squares from which a piece of ours checks their king. Attack
        // relations are symmetric for knights and kings, and reverse with the
        // colour for pawns, so these read straight off the king square.
        int theirKing = board.KingSquare(them);
        ulong pawnChecks = Attacks.Pawn(them, theirKing);
        ulong knightChecks = Attacks.Knight(theirKing);
        ulong kingChecks = Attacks.King(theirKing);

        // Empty-board rays: the superset filter for sliders.
        ulong rookLines = Attacks.Rook(theirKing, 0);
        ulong bishopLines = Attacks.Bishop(theirKing, 0);

        return new QuietOrderingContext(pawnThreats, minorThreats, rookThreats,
                                        pawnChecks, knightChecks, kingChecks,
                                        rookLines, bishopLines);
    }

    // The reference move picker rewards direct checks from the moved piece;
    // discovered checks are left to the full search's gives-check test.
    private static bool GivesDirectCheck(Board board, Move move, PieceType mover,
                                         in QuietOrderingContext ctx)
    {
        int to = move.To;
        ulong toBB = Bitboard.SquareBB(to);

        // Occupancy-independent movers resolve with a single test.
        switch (mover)
        {
            case PieceType.Pawn: return (ctx.PawnChecks & toBB) != 0;
            case PieceType.Knight: return (ctx.KnightChecks & toBB) != 0;
            case PieceType.King: return (ctx.KingChecks & toBB) != 0;
            case PieceType.Bishop:
                if ((ctx.BishopLines & toBB) == 0) return false;
                break;
            case PieceType.Rook:
                if ((ctx.RookLines & toBB) == 0) return false;
                break;
            case PieceType.Queen:
                if (((ctx.BishopLines | ctx.RookLines) & toBB) == 0) return false;
                break;
            default: return false;
        }

        // Aligned with the king, so the line might be blocked: only here is the
        // occupancy-aware attack set worth generating. The occupancy is the one
        // AFTER the move, since the mover vacating 'from' can itself be what
        // opens the line.
        Color us = board.SideToMove;
        ulong king = Bitboard.SquareBB(board.KingSquare(Board.OppositeColor(us)));
        ulong occ = (board.AllOccupancy & ~Bitboard.SquareBB(move.From)) | toBB;
        ulong attacks = mover switch
        {
            PieceType.Bishop => Attacks.Bishop(to, occ),
            PieceType.Rook => Attacks.Rook(to, occ),
            PieceType.Queen => Attacks.Queen(to, occ),
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
