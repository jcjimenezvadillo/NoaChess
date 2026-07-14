using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Pawn structure evaluation with a dedicated cache ("pawn hash").
//
// Terms evaluated (see EvaluationParams for the values):
// - Connected pawns: supported and/or phalanx pawns score a rank-indexed
//   formula that also counts whether the pawn is opposed and how many
//   supporters it has.
// - Isolated pawns: no neighbours at all; extra WeakUnopposed when the file
//   ahead is free (rook target), or the trebled-pawn Doubled special case.
// - Backward pawns: all neighbours strictly ahead and the pawn cannot advance
//   safely; extra WeakUnopposed off the rook files.
// - Doubled (own pawn directly behind, unsupported), WeakLever (unsupported
//   pawn attacked by two enemy pawns), DoubledEarly (doubled while the enemy
//   structure is still fluid).
// - Blocked pawns on relative ranks 5-6: cramping bonus.
// - Passed pawns: full passed definition (levers, lever-pushes vs phalanx,
//   blocked candidates), with a rank bonus (endgame-heavy).
//
// Why a cache: pawns move rarely, so consecutive positions in the search tree
// almost always share the exact same pawn formation. The structure score is
// stored under the board's pawn-only Zobrist key and the hit rate is huge —
// the (moderately expensive) computation below runs only on formation changes.
// The cached value is a tapered Score (middlegame + endgame).
public sealed class PawnStructureEvaluator
{
    // One slot per low-bits pattern of the pawn key. Small (1 MB-ish) because
    // distinct pawn formations in one search number in the thousands.
    private const int CacheSize = 1 << 16;

    // Each slot also caches the passer bitboards (the piece-dependent passer
    // terms in ClassicalEvaluator need them) and the outpost squares of both
    // colors (pawn-only inputs: pawn attacks, pawn shields and the enemy
    // pawn-attacks-span). Recomputing either per eval call costs real speed.
    private readonly (ulong Key, int Mg, int Eg, ulong WhitePassers, ulong BlackPassers,
                      ulong WhiteOutposts, ulong BlackOutposts)[] _cache
        = new (ulong, int, int, ulong, ulong, ulong, ulong)[CacheSize];

    // Masks precomputed once: file of a square, adjacent files, and the
    // "passed pawn" cone (squares ahead on own + adjacent files, per color).
    // Internal so ClassicalEvaluator can reuse them for the piece-dependent
    // passer terms (blocker, rook behind) that cannot live in the pawn cache.
    internal static readonly ulong[] FileMask = new ulong[8];
    internal static readonly ulong[] AdjacentFilesMask = new ulong[8];
    internal static readonly ulong[,] PassedPawnMask = new ulong[2, 64];

    // True when the pawn on 'sq' has no enemy pawns ahead on its own or the
    // adjacent files — the same test the cached evaluation uses.

    static PawnStructureEvaluator()
    {
        // Two passes: AdjacentFilesMask[f] reads FileMask[f + 1], which is not
        // filled yet if both are computed in the same loop iteration.
        for (int f = 0; f < 8; f++)
            FileMask[f] = Bitboard.FileA << f;
        for (int f = 0; f < 8; f++)
            AdjacentFilesMask[f] = (f > 0 ? FileMask[f - 1] : 0) | (f < 7 ? FileMask[f + 1] : 0);

        for (int sq = 0; sq < 64; sq++)
        {
            int file = Squares.FileOf(sq);
            int rank = Squares.RankOf(sq);
            ulong lanes = FileMask[file] | AdjacentFilesMask[file];

            // Everything strictly ahead of the pawn from each color's view.
            ulong ahead = 0, behind = 0;
            for (int r = rank + 1; r < 8; r++) ahead |= 0xFFUL << (r * 8);
            for (int r = 0; r < rank; r++) behind |= 0xFFUL << (r * 8);

            PassedPawnMask[(int)Color.White, sq] = lanes & ahead;
            PassedPawnMask[(int)Color.Black, sq] = lanes & behind;
        }
    }

    // Pawn structure score (White's point of view), cached by pawn key.
    public Score Evaluate(Board board) => Evaluate(board, out _, out _, out _, out _);

    public Score Evaluate(Board board, out ulong whitePassers, out ulong blackPassers)
        => Evaluate(board, out whitePassers, out blackPassers, out _, out _);

    // Same, also returning the cached passed-pawn bitboards of both sides (so
    // the caller's passer/piece terms only visit actual passers) and the
    // outpost squares of both colors.
    public Score Evaluate(Board board, out ulong whitePassers, out ulong blackPassers,
                          out ulong whiteOutposts, out ulong blackOutposts)
    {
        int slot = (int)(board.PawnZobristKey & (CacheSize - 1));
        (ulong key, int mg, int eg, ulong wp, ulong bp, ulong wo, ulong bo) = _cache[slot];
        if (key == board.PawnZobristKey)
        {
            whitePassers = wp;
            blackPassers = bp;
            whiteOutposts = wo;
            blackOutposts = bo;
            return new Score(mg, eg);
        }

        Score score = EvaluateSide(board, Color.White, out whitePassers)
                    - EvaluateSide(board, Color.Black, out blackPassers);
        ComputeOutposts(board, out whiteOutposts, out blackOutposts);
        _cache[slot] = (board.PawnZobristKey, score.Mg, score.Eg, whitePassers, blackPassers,
                        whiteOutposts, blackOutposts);
        return score;
    }

    // Outpost squares per color: relative ranks 4-6, protected by an own pawn
    // OR with any pawn directly in front (shield), and outside the enemy's
    // pawn-attacks-span (no enemy pawn can ever kick the piece out).
    private static void ComputeOutposts(Board board, out ulong whiteOutposts, out ulong blackOutposts)
    {
        ulong whitePawns = board.Pieces(Color.White, PieceType.Pawn);
        ulong blackPawns = board.Pieces(Color.Black, PieceType.Pawn);
        ulong whiteAttacks = ((whitePawns & ~Bitboard.FileA) << 7) | ((whitePawns & ~Bitboard.FileH) << 9);
        ulong blackAttacks = ((blackPawns & ~Bitboard.FileA) >> 9) | ((blackPawns & ~Bitboard.FileH) >> 7);
        ulong allPawns = whitePawns | blackPawns;

        ulong whiteSpan = PawnAttacksSpan(Color.White, whitePawns, blackPawns, whiteAttacks);
        ulong blackSpan = PawnAttacksSpan(Color.Black, blackPawns, whitePawns, blackAttacks);

        whiteOutposts = ((0xFFUL << 24) | (0xFFUL << 32) | (0xFFUL << 40)) // ranks 4-6
                      & (whiteAttacks | (allPawns >> 8))
                      & ~blackSpan;
        blackOutposts = ((0xFFUL << 16) | (0xFFUL << 24) | (0xFFUL << 32)) // ranks 5-3
                      & (blackAttacks | (allPawns << 8))
                      & ~whiteSpan;
    }

    // pawn-attacks-span: every square this color's pawns attack
    // now plus, for pawns that are neither blocked nor backward, every square
    // they could ever attack while advancing. A blocked or backward pawn will
    // never advance past its blockade, so it cannot evict a piece parked ahead
    // of its file — those pawns only contribute their current attacks.
    private static ulong PawnAttacksSpan(Color us, ulong ourPawns, ulong theirPawns, ulong ownAttacks)
    {
        bool white = us == Color.White;
        ulong span = ownAttacks;
        ulong b = ourPawns;

        while (b != 0)
        {
            int s = Bitboard.PopLsb(ref b);
            int rank = Squares.RankOf(s);
            int push = white ? s + 8 : s - 8; // A pawn always has a push square.
            ulong pushBB = Bitboard.SquareBB(push);

            // Blocked: an enemy pawn sits directly in front.
            bool blocked = (theirPawns & pushBB) != 0;

            // Backward: behind all neighbour pawns on the adjacent files and
            // unable to advance safely (the push square is blocked or covered
            // by an enemy pawn).
            ulong neighbours = ourPawns & AdjacentFilesMask[Squares.FileOf(s)];
            ulong notAhead = white
                ? (1UL << ((rank + 1) * 8)) - 1 // Own rank and below (rank <= 6 for a pawn).
                : ~((1UL << (rank * 8)) - 1);   // Own rank and above (rank >= 1).
            ulong leverPush = theirPawns & (white
                ? ((pushBB & ~Bitboard.FileA) << 7) | ((pushBB & ~Bitboard.FileH) << 9)
                : ((pushBB & ~Bitboard.FileA) >> 9) | ((pushBB & ~Bitboard.FileH) >> 7));
            bool backward = (neighbours & notAhead) == 0 && (leverPush != 0 || blocked);

            if (!backward && !blocked)
                span |= PassedPawnMask[(int)us, s] & AdjacentFilesMask[Squares.FileOf(s)];
        }
        return span;
    }

    // Attack squares of an arbitrary pawn set of the given color.
    private static ulong PawnAttacks(Color color, ulong pawns) => color == Color.White
        ? ((pawns & ~Bitboard.FileA) << 7) | ((pawns & ~Bitboard.FileH) << 9)
        : ((pawns & ~Bitboard.FileA) >> 9) | ((pawns & ~Bitboard.FileH) >> 7);

    private static Score EvaluateSide(Board board, Color color, out ulong passersOut)
    {
        bool white = color == Color.White;
        ulong ourPawns = board.Pieces(color, PieceType.Pawn);
        ulong theirPawns = board.Pieces(Board.OppositeColor(color), PieceType.Pawn);
        Score score = default;

        // Squares their pawns attack (and attack twice): used by the
        // candidate-passer help condition and the DoubledEarly gate below.
        ulong theirLeft = white
            ? (theirPawns & ~Bitboard.FileA) >> 9
            : (theirPawns & ~Bitboard.FileA) << 7;
        ulong theirRight = white
            ? (theirPawns & ~Bitboard.FileH) >> 7
            : (theirPawns & ~Bitboard.FileH) << 9;
        ulong theirDoubleAttacks = theirLeft & theirRight;
        ulong theirAttacks = theirLeft | theirRight;

        // DoubledEarly fires while NO enemy pawn is fixed: none of our pawns
        // rams an enemy pawn or stands on a square their pawns cover. Once
        // the structures lock, doubling can be a legitimate byproduct of a
        // capture toward the center; before that it is just a weakness.
        bool noEnemyPawnFixed = (ourPawns
            & (white ? (theirPawns | theirAttacks) >> 8
                     : (theirPawns | theirAttacks) << 8)) == 0;

        // Per-pawn terms. Passers collected so the connected-passers bonus
        // can look at the full set afterwards.
        ulong pawns = ourPawns;
        ulong passers = 0;
        while (pawns != 0)
        {
            int sq = Bitboard.PopLsb(ref pawns);
            int file = Squares.FileOf(sq);
            int rank = Squares.RankOf(sq);
            int relativeRank = white ? rank : 7 - rank;
            int stopSq = white ? sq + 8 : sq - 8;

            ulong sqBB = Bitboard.SquareBB(sq);
            ulong stopBB = Bitboard.SquareBB(stopSq);
            ulong rankMask = 0xFFUL << (rank * 8);

            ulong opposed = theirPawns & PassedPawnMask[(int)color, sq] & FileMask[file];
            ulong blocked = theirPawns & stopBB;
            ulong stoppers = theirPawns & PassedPawnMask[(int)color, sq];
            ulong lever = theirPawns & PawnAttacks(color, sqBB);
            ulong leverPush = theirPawns & PawnAttacks(color, stopBB);
            ulong doubled = ourPawns & (white ? sqBB >> 8 : sqBB << 8);
            ulong neighbours = ourPawns & AdjacentFilesMask[file];
            ulong phalanx = neighbours & rankMask;
            ulong support = neighbours & (white ? rankMask >> 8 : rankMask << 8);

            if (doubled != 0 && noEnemyPawnFixed)
                score -= EvaluationParams.DoubledEarly;

            // Backward: every neighbour is strictly ahead (a level phalanx
            // member or one behind could advance to help) AND the pawn cannot
            // step forward (blocked, or the stop square is covered by an
            // enemy pawn one push away).
            ulong levelOrBehind = white
                ? (1UL << ((rank + 1) * 8)) - 1     // ranks 0..rank
                : ~((1UL << (rank * 8)) - 1);        // ranks rank..7
            bool backward = (neighbours & levelOrBehind) == 0
                && (leverPush | blocked) != 0;

            // Scoring chain — the branches are mutually exclusive on purpose:
            // a pawn is either connected, isolated (no neighbours) or backward.
            if ((support | phalanx) != 0)
            {
                // Connected formula in RAW reference units, converted x0.48 at
                // the end: base grows with rank, doubles for a phalanx member,
                // shrinks when opposed, +22 per direct supporter; the endgame
                // half only kicks in from relative rank 3 up.
                int v = EvaluationParams.Connected[relativeRank]
                            * (2 + (phalanx != 0 ? 1 : 0) - (opposed != 0 ? 1 : 0))
                        + 22 * Bitboard.PopCount(support);
                score += new Score(v * 100 / 208,
                                   v * (relativeRank - 2) / 4 * 100 / 208);
            }
            else if (neighbours == 0)
            {
                // A trebled/doubled isolated pawn behind an own pawn on a file
                // with no enemy neighbours pays the (heavier) Doubled penalty
                // instead; other isolani pay Isolated, plus WeakUnopposed when
                // the file ahead is free (a rook target that cannot be traded).
                ulong ownAheadHere = ourPawns & PassedPawnMask[(int)color, sq] & FileMask[file];
                if (opposed != 0 && ownAheadHere != 0
                    && (theirPawns & AdjacentFilesMask[file]) == 0)
                    score -= EvaluationParams.Doubled;
                else
                    score -= EvaluationParams.Isolated
                           + EvaluationParams.WeakUnopposed * (opposed == 0 ? 1 : 0);
            }
            else if (backward)
            {
                score -= EvaluationParams.Backward
                       + EvaluationParams.WeakUnopposed
                             * (opposed == 0 && file != 0 && file != 7 ? 1 : 0);
            }

            // Unsupported pawns: doubled directly behind an own pawn, and the
            // weak lever (attacked by two enemy pawns — whichever way we
            // recapture, we lose the exchange of pawns).
            if (support == 0)
            {
                score -= EvaluationParams.Doubled * (doubled != 0 ? 1 : 0)
                       + EvaluationParams.WeakLever
                             * (Bitboard.PopCount(lever) > 1 ? 1 : 0);
            }

            // A blocked pawn on relative rank 5-6 cramps the defense.
            if (blocked != 0 && relativeRank >= 4)
                score += EvaluationParams.BlockedPawnRank[relativeRank - 4];

            // Passed pawn — reference definition, wider than the classic mask
            // test. A pawn is passed when one of three conditions holds:
            //   (a) no stoppers at all except pawns we can capture right now
            //       (levers);
            //   (b) the only stoppers are lever-pushes (enemy pawns that would
            //       attack our stop square) and our phalanx outnumbers them;
            //   (c) the only stopper is the blocker directly in front, we are
            //       on relative rank 5+, and a supporting pawn can safely step
            //       up to offer the freeing trade (candidate passer — refined
            //       further by the piece-aware blocked-passer filter).
            // A pawn behind an own pawn on the same file is never passed.
            ulong ownAhead = ourPawns & PassedPawnMask[(int)color, sq] & FileMask[file];

            bool passed = ownAhead == 0
                && (stoppers == lever
                    || (stoppers == leverPush
                        && Bitboard.PopCount(phalanx) >= Bitboard.PopCount(leverPush))
                    || (stoppers == blocked && blocked != 0 && relativeRank >= 4
                        && ((white ? support << 8 : support >> 8)
                            & ~(theirPawns | theirDoubleAttacks)) != 0));

            if (passed)
            {
                passers |= sqBB;
                score += EvaluationParams.PassedPawn[relativeRank];
            }
        }

        // Connected passers: each passer with a friendly passer on an adjacent
        // file gets a bonus — together they escort each other home.
        ulong p = passers;
        while (p != 0)
        {
            int sq = Bitboard.PopLsb(ref p);
            if ((passers & AdjacentFilesMask[Squares.FileOf(sq)]) != 0)
                score += EvaluationParams.ConnectedPassers;
        }

        passersOut = passers;
        return score;
    }
}
