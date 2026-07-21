using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Piece-square tables (PST): a small bonus/penalty depending on the square a
// piece occupies. They encode basic positional knowledge without computing
// it: "knights are worth more in the center", "the king is safer castled".
//
// The tables are written FROM WHITE'S POINT OF VIEW, the way a board diagram
// is read: the first row of each array is rank 8. To read the value for a
// white piece the rank is flipped (see Value); for black the index matches
// directly, so both sides use the same table symmetrically.
// Typical public-domain values, rounded.
public static class PieceSquareTables
{
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

    private static readonly int[][] TableByPiece =
        [PawnPst, KnightPst, BishopPst, RookPst, QueenPst, KingPst];

    // PST value for a piece of the given type and color on a square.
    public static int Value(PieceType type, Color color, int square)
    {
        int rank = Squares.RankOf(square);
        int file = Squares.FileOf(square);
        int index = color == Color.White
            ? (7 - rank) * 8 + file
            : rank * 8 + file;
        return TableByPiece[(int)type][index];
    }
}
