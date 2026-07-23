using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Pawn structure evaluation with a dedicated cache ("pawn hash").
//
// Terms evaluated (see EvaluationParams for the values):
// - Doubled pawns: extra pawns on the same file.
// - Isolated pawns: no friendly pawns on adjacent files.
// - Phalanx: friendly pawn on the same rank and adjacent file (side-by-side).
// - Backward pawns: stop square attacked by enemy, no friendly pawn level or
//   behind on adjacent files; not isolated (that has its own penalty).
// - Passed pawns: no enemy pawns ahead on the same or adjacent files, with a
//   bonus that grows with the rank (endgame-heavy: a passer wins endgames).
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

    // Each slot also caches the passer bitboards: the piece-dependent passer
    // terms in ClassicalEvaluator need them, and recomputing IsPassed for
    // every pawn on every eval call costs real search speed.
    private readonly (ulong Key, int Mg, int Eg, ulong WhitePassers, ulong BlackPassers)[] _cache
        = new (ulong, int, int, ulong, ulong)[CacheSize];

    // Masks precomputed once: file of a square, adjacent files, and the
    // "passed pawn" cone (squares ahead on own + adjacent files, per color).
    // Internal so ClassicalEvaluator can reuse them for the piece-dependent
    // passer terms (blocker, rook behind) that cannot live in the pawn cache.
    internal static readonly ulong[] FileMask = new ulong[8];
    internal static readonly ulong[] AdjacentFilesMask = new ulong[8];
    internal static readonly ulong[,] PassedPawnMask = new ulong[2, 64];

    // True when the pawn on 'sq' has no enemy pawns ahead on its own or the
    // adjacent files — the same test the cached evaluation uses.
    internal static bool IsPassed(Color color, int sq, ulong theirPawns)
        => (theirPawns & PassedPawnMask[(int)color, sq]) == 0;

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
    public Score Evaluate(Board board) => Evaluate(board, out _, out _);

    // Same, also returning the cached passed-pawn bitboards of both sides so
    // the caller's passer/piece terms only visit actual passers.
    public Score Evaluate(Board board, out ulong whitePassers, out ulong blackPassers)
    {
        int slot = (int)(board.PawnZobristKey & (CacheSize - 1));
        (ulong key, int mg, int eg, ulong wp, ulong bp) = _cache[slot];
        if (key == board.PawnZobristKey)
        {
            whitePassers = wp;
            blackPassers = bp;
            return new Score(mg, eg);
        }

        Score score = EvaluateSide(board, Color.White, out whitePassers)
                    - EvaluateSide(board, Color.Black, out blackPassers);
        _cache[slot] = (board.PawnZobristKey, score.Mg, score.Eg, whitePassers, blackPassers);
        return score;
    }

    private static Score EvaluateSide(Board board, Color color, out ulong passersOut)
    {
        ulong ourPawns = board.Pieces(color, PieceType.Pawn);
        ulong theirPawns = board.Pieces(Board.OppositeColor(color), PieceType.Pawn);
        Score score = default;

        // Enemy pawn attacks: used for the backward-pawn check below.
        ulong theirPawnAttacks = color == Color.White
            ? ((theirPawns & ~Bitboard.FileA) >> 9) | ((theirPawns & ~Bitboard.FileH) >> 7)
            : ((theirPawns & ~Bitboard.FileA) << 7) | ((theirPawns & ~Bitboard.FileH) << 9);

        // Doubled pawns: counted per file (each pawn beyond the first).
        for (int f = 0; f < 8; f++)
        {
            int pawnsOnFile = Bitboard.PopCount(ourPawns & FileMask[f]);
            if (pawnsOnFile > 1)
                score += EvaluationParams.DoubledPawn * (pawnsOnFile - 1);
        }

        // Per-pawn terms. Passers collected so the connected-passers bonus
        // can look at the full set afterwards.
        ulong pawns = ourPawns;
        ulong passers = 0;
        while (pawns != 0)
        {
            int sq = Bitboard.PopLsb(ref pawns);
            int file = Squares.FileOf(sq);
            int rank = Squares.RankOf(sq);
            int relativeRank = color == Color.White ? rank : 7 - rank;

            // Isolated pawn: no friendly pawn on adjacent files.
            bool isolated = (ourPawns & AdjacentFilesMask[file]) == 0;
            if (isolated)
                score += EvaluationParams.IsolatedPawn;

            // Phalanx: friendly pawn on the same rank and adjacent file.
            ulong rankMask = 0xFFUL << (rank * 8);
            if ((ourPawns & AdjacentFilesMask[file] & rankMask) != 0)
                score += EvaluationParams.Phalanx[relativeRank];

            // Backward pawn: stop square attacked by an enemy pawn AND no
            // friendly pawn on adjacent files at the SAME rank or behind (a
            // level neighbour defends the stop square directly — a phalanx
            // member is never backward — and one behind can advance to help).
            // Exclusive of isolated: an isolated pawn trivially has no support
            // and already pays its own penalty; stacking both double-counts.
            int stopSq = color == Color.White ? sq + 8 : sq - 8;
            ulong supportMask = color == Color.White
                ? (1UL << ((rank + 1) * 8)) - 1     // ranks 0..rank
                : ~((1UL << (rank * 8)) - 1);        // ranks rank..7
            if (!isolated
                && (theirPawnAttacks & Bitboard.SquareBB(stopSq)) != 0
                && (ourPawns & AdjacentFilesMask[file] & supportMask) == 0)
            {
                score += EvaluationParams.BackwardPawn;
            }

            // Passed pawn: no enemy pawn ahead on same or adjacent files.
            if (IsPassed(color, sq, theirPawns))
            {
                passers |= Bitboard.SquareBB(sq);
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
