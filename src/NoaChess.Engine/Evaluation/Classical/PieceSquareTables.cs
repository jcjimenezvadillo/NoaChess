using NoaChess.Core;

namespace NoaChess.Engine.Evaluation.Classical;

// Piece-square tables (PST): a bonus/penalty depending on the square a piece
// occupies. They encode basic positional knowledge without computing it:
// "knights belong in the center", "the king hides in the corner in the
// middlegame but marches to the center in the endgame".
//
// Two tables per piece: a MIDDLEGAME table and an ENDGAME table. The final
// value is interpolated by the game phase (tapered eval). This is what lets
// the king score +30 for castling with queens on the board yet +25 for
// standing on e4 once it is a king-and-pawn endgame — a single-phase table
// can only ever pick one of those.
//
// Started from the well-known PeSTO tables (public domain, derived from
// Rofchade) and texel-tuned (v2.4.5, tools/NoaChess.Tuner) on fresh self-play
// data from the v2.4.5-strength engine, jointly with the positional terms in
// EvaluationParams. Each table is written FROM WHITE'S POINT OF VIEW with the
// first row = rank 8, the way a board diagram reads. Value() flips the rank
// for White and indexes directly for Black, so both sides use the same table
// symmetrically.
public static class PieceSquareTables
{
    private static readonly int[] PawnMg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
         82, 106,  45,  83,  40,  98,   6, -39,
         10,  -3,  28,  41,  37,  44,   5,   8,
         -8,   7,  10,  27,  23,  16,   3, -19,
        -21,  -4,   1,  14,  13,   8,   0, -25,
        -14,   2,   2,  -2,  11,   7,  33,  -8,
        -33,  -3, -12, -21, -15,  28,  42, -18,
          0,   0,   0,   0,   0,   0,   0,   0,
    ];

    private static readonly int[] PawnEg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
        150, 145, 130, 106, 119, 104, 137, 159,
         74,  88,  69,  47,  28,  25,  54,  58,
         40,  26,  15,   5,   2,  -2,  29,  21,
         27,  19,  -1,  -3,  -5,  -2,  19,  13,
         16,  11,  -6, -11,  -4,   5,   1,   6,
         29,  20,  16,   2,  11,   6,   8,  19,
          0,   0,   0,   0,   0,   0,   0,   0,
    ];

    private static readonly int[] KnightMg =
    [
        -139, -87, -32, -21,  71, -125,   3, -79,
         -45, -23,  52,  64,  51,   90,  -1,   9,
         -19,  68,  43,  73,  96,  129,  87,  34,
           3,  29,  31,  59,  57,   89,  42,  50,
          -1,  -8,  22,  27,  56,   45,  33,  20,
         -29,   7,  12,  30,  37,   29,  45,  -4,
          -9, -39,   2,  11,  19,   14, -14, -15,
         -89, -25, -48,  -7, -13,   -4,  -9, -23,
    ];

    private static readonly int[] KnightEg =
    [
        -38, -24,  -5, -24, -41, -19, -63, -103,
        -25, -12, -15, -12,  -9, -25, -48,  -36,
        -18,   6,   8,   9,  -9,  -9, -19,  -33,
        -31,  -3,  16,   6,   2,  -7,  10,  -22,
        -14, -14,  12,  17,  -4,   5,  -4,  -36,
        -11,  -5,   1,   3,  10,  -3, -28,  -22,
        -54, -26,  -4,  -7,  -4, -22, -51,  -26,
         -1, -61, -23, -17, -22, -46, -42,  -84,
    ];

    private static readonly int[] BishopMg =
    [
         -1,  32, -74, -33,   3, -14, -17,  16,
          2,   8,  10,  15,  42,  49, -10, -19,
         -6,  45,  61,  52,  39,  78,  51,  22,
         14,  27,  35,  70,  45,  57,  25,  14,
         14,  21,  29,  50,  44,  16,  14,  18,
         16,  19,  21,  23,  20,  39,  30,  12,
          6,  17,  34,   6,  21,  33,  49,  15,
        -15, -19, -10, -15, -23, -14, -11,   3,
    ];

    private static readonly int[] BishopEg =
    [
        -20, -23, -31,  -6,  -3,  -3, -19, -52,
        -36, -32,  -3, -16, -17, -13, -14, -42,
        -10,  -8, -20,   5, -20,   6,   0,   6,
        -15,   1,  12,  -7,   6, -18,  15,   2,
        -10,  13,   3,   1,  -5,  12,  -3, -25,
         -6,  -1,   0,  14,   9,  -5,  -7, -23,
        -38,  -2, -33,  -7, -22, -33, -33, -37,
        -51, -29, -33, -21, -23, -36, -33, -45,
    ];

    private static readonly int[] RookMg =
    [
         52,  70,  24,  59,  51,  19,  33,  59,
         27,  36,  58,  66,  88,  71,  28,  28,
         13,  39,  42,  48,  45,  73,  61,  16,
         -8, -11,  19,  34,  44,  35,  14, -12,
        -16, -34, -12, -11,   1,   3,   6, -11,
        -23, -25, -24, -39, -15, -12,   3, -25,
        -36,  -4, -26,  -9,  13,  -5,   6, -69,
         -7,  -3,   5,  19,  24,  15, -41, -26,
    ];

    private static readonly int[] RookEg =
    [
        -11,  -4,   4,   3,  -8,   8,   0,  -1,
          5,   5,  -7,  -9, -29, -17,  -2,  -5,
         21,  21,  15,   1,   4,  17,  13,  11,
         28,  19,  23,  13,   8,  13,  13,  24,
         23,  27,  18,  12,  15,  14,  12,   1,
         -2,  -6,  -3,   5, -11,  -6,  -4,  -2,
        -24, -26,  -8, -10, -23,  -5, -23,  -9,
          5,  -6,  -3,  -7, -15,  -9,  14,  -2,
    ];

    private static readonly int[] QueenMg =
    [
         -6,   0,  29,  32,  51,  50,  17,  17,
        -24, -19, -11,  17,   4,  29,  24,  56,
          3,  11,  13,  20,  21,  66,  23,  33,
        -25,   1,   0,   4,  25,   7,   8,  15,
          1,  -4,   3,   8,  12, -14,  15,   1,
        -32,   4,   5,   0,   5,   4,  12, -13,
        -19,   0,  23,  10,  22,  35,  23, -27,
        -15,  -8,   1,   6, -11, -53, -59, -38,
    ];

    private static readonly int[] QueenEg =
    [
          7,  28,   4,  13,   7,  23, -16,  12,
          1,  48,  58,  59,  60,  37,  18,   4,
        -20,  34,  37,  49,  47,  31,  27,   5,
          7,  50,  52,  45,  45,  58,  53,  32,
          2,  54,  33,  57,  19,  54,  51,  17,
        -16,  -5,  17,  16,  15,  29,  18,  -5,
        -32, -31, -48, -32, -32, -51, -64, -44,
        -61, -56, -30, -59, -33, -60, -48, -19,
    ];

    private static readonly int[] KingMg =
    [
        -93,  51,  44,  13, -56,  -6,  30,  41,
         29,  -1,   8,  11,  20,  -4, -10,  -1,
         -9,  24,   2,  12,   0,   6,  20, -12,
        -17, -20, -24, -25, -30, -23,  10, -44,
        -61,  -3,  -7, -31, -44, -32,  -9, -59,
        -42,  12,  -6, -30, -36,  -2,  -1, -35,
          1,  -3,  -6, -48, -43,  -2,  19,  18,
        -43,  24,   8, -58,  -8, -42,  40,  20,
    ];

    private static readonly int[] KingEg =
    [
        -74,  -7,  10,  10,   5,  15,  14,  -3,
         14,  17,  42,  45,  45,  38,  23,  27,
         10,  17,  23,  43,  48,  45,  34,  37,
         -8,  22,  24,  27,  26,  33,  38,  19,
        -14,   0,   7,  24,  27,  23,  19,   5,
          3,   1,   3,  17,  23,  16,  13,  -5,
        -11,   9,   6,  17,  22,   8,  -3, -17,
        -25, -30,  -9, -15, -36, -20, -30, -57,
    ];

    // Public so the texel tuner (tools/NoaChess.Tuner) can adjust the table
    // entries in-process; the engine itself never writes to them.
    public static readonly int[][] MgByPiece =
        [PawnMg, KnightMg, BishopMg, RookMg, QueenMg, KingMg];
    public static readonly int[][] EgByPiece =
        [PawnEg, KnightEg, BishopEg, RookEg, QueenEg, KingEg];

    // Index into a white-oriented table for a piece of the given color/square.
    private static int Index(Color color, int square)
    {
        int rank = Squares.RankOf(square);
        int file = Squares.FileOf(square);
        return color == Color.White ? (7 - rank) * 8 + file : rank * 8 + file;
    }

    // Middlegame and endgame PST values for a piece, as a tapered Score.
    public static Score Value(PieceType type, Color color, int square)
    {
        int i = Index(color, square);
        int t = (int)type;
        return new Score(MgByPiece[t][i], EgByPiece[t][i]);
    }
}
