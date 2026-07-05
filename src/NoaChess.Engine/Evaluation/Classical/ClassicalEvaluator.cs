using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Initial classical evaluator: material + piece-square tables (PST).
//
// - Material: each piece type has a fixed value in centipawns.
// - PST (Piece-Square Tables): a small bonus/penalty depending on the square a
//   piece occupies. They encode basic positional knowledge without computing
//   it: "knights are worth more in the center", "the king is safer castled", etc.
//
// It is deliberately simple: enough for the engine to play sensibly in the
// MVP. Pawn structure, mobility, king safety, etc. arrive in v1.0.
public sealed class ClassicalEvaluator : IPositionEvaluator
{
    // Material values in centipawns (index = PieceType).
    // The king has no material value: it is never captured (mate is detected in the search).
    private static readonly int[] MaterialValue = [100, 320, 330, 500, 900, 0];

    // Piece-square tables FROM WHITE'S POINT OF VIEW, written the way a board
    // diagram is read: the first row of the array is rank 8. To read the value
    // for a white square the rank must be "flipped" (see PstValue). For black
    // the table is used as-is thanks to the symmetry.
    // Typical public-domain values, rounded.

    private static readonly int[] PawnPst =
    [
         0,  0,  0,  0,  0,  0,  0,  0,
        50, 50, 50, 50, 50, 50, 50, 50,   // Pawns about to promote
        10, 10, 20, 30, 30, 20, 10, 10,
         5,  5, 10, 25, 25, 10,  5,  5,   // Encourages occupying the center (d4/e4)
         0,  0,  0, 20, 20,  0,  0,  0,
         5, -5,-10,  0,  0,-10, -5,  5,
         5, 10, 10,-20,-20, 10, 10,  5,   // Penalizes blocking d2/e2
         0,  0,  0,  0,  0,  0,  0,  0
    ];

    private static readonly int[] KnightPst =
    [
       -50,-40,-30,-30,-30,-30,-40,-50,
       -40,-20,  0,  0,  0,  0,-20,-40,
       -30,  0, 10, 15, 15, 10,  0,-30,
       -30,  5, 15, 20, 20, 15,  5,-30,
       -30,  0, 15, 20, 20, 15,  0,-30,
       -30,  5, 10, 15, 15, 10,  5,-30,
       -40,-20,  0,  5,  5,  0,-20,-40,
       -50,-40,-30,-30,-30,-30,-40,-50    // "A knight on the rim is dim"
    ];

    private static readonly int[] BishopPst =
    [
       -20,-10,-10,-10,-10,-10,-10,-20,
       -10,  0,  0,  0,  0,  0,  0,-10,
       -10,  0,  5, 10, 10,  5,  0,-10,
       -10,  5,  5, 10, 10,  5,  5,-10,
       -10,  0, 10, 10, 10, 10,  0,-10,
       -10, 10, 10, 10, 10, 10, 10,-10,
       -10,  5,  0,  0,  0,  0,  5,-10,
       -20,-10,-10,-10,-10,-10,-10,-20
    ];

    private static readonly int[] RookPst =
    [
         0,  0,  0,  0,  0,  0,  0,  0,
         5, 10, 10, 10, 10, 10, 10,  5,   // Rook on the 7th rank
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
        -5,  0,  0,  0,  0,  0,  0, -5,
         0,  0,  0,  5,  5,  0,  0,  0    // Centralized rooks after castling
    ];

    private static readonly int[] QueenPst =
    [
       -20,-10,-10, -5, -5,-10,-10,-20,
       -10,  0,  0,  0,  0,  0,  0,-10,
       -10,  0,  5,  5,  5,  5,  0,-10,
        -5,  0,  5,  5,  5,  5,  0, -5,
         0,  0,  5,  5,  5,  5,  0, -5,
       -10,  5,  5,  5,  5,  5,  0,-10,
       -10,  0,  5,  0,  0,  0,  0,-10,
       -20,-10,-10, -5, -5,-10,-10,-20
    ];

    private static readonly int[] KingPst =
    [
       -30,-40,-40,-50,-50,-40,-40,-30,
       -30,-40,-40,-50,-50,-40,-40,-30,
       -30,-40,-40,-50,-50,-40,-40,-30,
       -30,-40,-40,-50,-50,-40,-40,-30,
       -20,-30,-30,-40,-40,-30,-30,-20,
       -10,-20,-20,-20,-20,-20,-20,-10,
        20, 20,  0,  0,  0,  0, 20, 20,
        20, 30, 10,  0,  0, 10, 30, 20    // Rewards the castled king (g1/c1)
    ];

    private static readonly int[][] PstByPiece =
        [PawnPst, KnightPst, BishopPst, RookPst, QueenPst, KingPst];

    public int Evaluate(Board board)
    {
        int score = 0; // Positive = white advantage (converted at the end).

        for (int c = 0; c < 2; c++)
        {
            Color color = (Color)c;
            int sign = color == Color.White ? 1 : -1;

            for (int p = 0; p < 6; p++)
            {
                ulong pieces = board.Pieces(color, (PieceType)p);
                while (pieces != 0)
                {
                    int sq = Bitboard.PopLsb(ref pieces);
                    score += sign * (MaterialValue[p] + PstValue(PstByPiece[p], color, sq));
                }
            }
        }

        // Negamax needs the score relative to the side to move.
        return board.SideToMove == Color.White ? score : -score;
    }

    private static int PstValue(int[] table, Color color, int square)
    {
        // The tables are written "as seen by white" (first entry = a8).
        // For white the rank is flipped; for black the index matches directly,
        // so both sides use the same table symmetrically.
        int rank = Squares.RankOf(square);
        int file = Squares.FileOf(square);
        int index = color == Color.White
            ? (7 - rank) * 8 + file
            : rank * 8 + file;
        return table[index];
    }
}
