using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Pawn structure evaluation with a dedicated cache ("pawn hash").
//
// Terms evaluated (see EvaluationParams for the values):
// - Doubled pawns: extra pawns on the same file.
// - Isolated pawns: no friendly pawns on adjacent files.
// - Passed pawns: no enemy pawns ahead on the same or adjacent files, with a
//   bonus that grows with the rank.
//
// Why a cache: pawns move rarely, so consecutive positions in the search tree
// almost always share the exact same pawn formation. The structure score is
// stored under the board's pawn-only Zobrist key and the hit rate is huge —
// the (moderately expensive) computation below runs only on formation changes.
public sealed class PawnStructureEvaluator
{
    // One slot per low-bits pattern of the pawn key. Small (1 MB-ish) because
    // distinct pawn formations in one search number in the thousands.
    private const int CacheSize = 1 << 16;

    private readonly (ulong Key, int Score)[] _cache = new (ulong, int)[CacheSize];

    // Masks precomputed once: file of a square, adjacent files, and the
    // "passed pawn" cone (squares ahead on own + adjacent files, per color).
    private static readonly ulong[] FileMask = new ulong[8];
    private static readonly ulong[] AdjacentFilesMask = new ulong[8];
    private static readonly ulong[,] PassedPawnMask = new ulong[2, 64];

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

    // Pawn structure score, positive = good for White. Cached by pawn key.
    public int Evaluate(Board board)
    {
        int slot = (int)(board.PawnZobristKey & (CacheSize - 1));
        (ulong key, int score) = _cache[slot];
        if (key == board.PawnZobristKey)
            return score;

        score = EvaluateSide(board, Color.White) - EvaluateSide(board, Color.Black);
        _cache[slot] = (board.PawnZobristKey, score);
        return score;
    }

    private static int EvaluateSide(Board board, Color color)
    {
        ulong ourPawns = board.Pieces(color, PieceType.Pawn);
        ulong theirPawns = board.Pieces(Board.OppositeColor(color), PieceType.Pawn);
        int score = 0;

        // Doubled pawns: counted per file (each pawn beyond the first).
        for (int f = 0; f < 8; f++)
        {
            int pawnsOnFile = Bitboard.PopCount(ourPawns & FileMask[f]);
            if (pawnsOnFile > 1)
                score += (pawnsOnFile - 1) * EvaluationParams.DoubledPawnPenalty;
        }

        // Isolated and passed pawns: per pawn.
        ulong pawns = ourPawns;
        while (pawns != 0)
        {
            int sq = Bitboard.PopLsb(ref pawns);
            int file = Squares.FileOf(sq);

            if ((ourPawns & AdjacentFilesMask[file]) == 0)
                score += EvaluationParams.IsolatedPawnPenalty;

            if ((theirPawns & PassedPawnMask[(int)color, sq]) == 0)
            {
                // Relative rank: how far the pawn has advanced from ITS side.
                int relativeRank = color == Color.White
                    ? Squares.RankOf(sq)
                    : 7 - Squares.RankOf(sq);
                score += EvaluationParams.PassedPawnBonus[relativeRank];
            }
        }

        return score;
    }
}
