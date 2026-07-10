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
// Rofchade) and texel-tuned (v2.4.0, tools/NoaChess.Tuner) on fresh self-play
// data from the v2.3.0-strength engine, jointly with the positional terms in
// EvaluationParams. Each table is written FROM WHITE'S POINT OF VIEW with the
// first row = rank 8, the way a board diagram reads. Value() flips the rank
// for White and indexes directly for Black, so both sides use the same table
// symmetrically.
public static class PieceSquareTables
{
    private static readonly int[] PawnMg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
         84, 120,  47,  85,  54, 112,  20, -25,
          4,   5,  24,  29,  51,  42,  19,  -6,
         -8,  13,  12,  25,  25,  26,  15, -15,
        -25,  -4,  -1,  16,  13,  12,  -2, -19,
        -22,  -4,  -4, -14,   3,   5,  35, -14,
        -33,  -3, -12, -15, -23,  24,  42, -22,
          0,   0,   0,   0,   0,   0,   0,   0,
    ];

    private static readonly int[] PawnEg =
    [
          0,   0,   0,   0,   0,   0,   0,   0,
        164, 159, 144, 120, 133, 118, 151, 173,
         88,  86,  71,  53,  42,  39,  68,  70,
         32,  32,  23,   9,  12,   6,  19,  21,
         21,  17,   7,   3,  -7,   2,  13,   5,
         16,  -3,   4,  -3, -12,  -3,   5,   0,
         27,  16,  18,  -4,   7,   2,   4,   7,
          0,   0,   0,   0,   0,   0,   0,   0,
    ];

    private static readonly int[] KnightMg =
    [
        -153, -75, -38, -35,  57, -111, -11, -93,
         -59, -27,  62,  50,  37,   76,  13,  -5,
         -33,  62,  51,  79,  98,  115,  73,  38,
          -3,  25,  33,  45,  43,   75,  32,  36,
         -15,   6,  16,  27,  42,   31,  19,   6,
         -17,   5,  10,  16,  33,   23,  31, -18,
         -23, -39,   2,  11,   9,   16, -20, -29,
        -103, -31, -62, -21, -19,  -14, -21,  -9,
    ];

    private static readonly int[] KnightEg =
    [
        -52, -36, -19, -14, -41, -33, -51, -95,
        -39,   2, -21,   0, -23, -11, -38, -38,
        -14,  -6,  -2,  23,   5, -15, -33, -47,
        -19,  -3,  30,  20,   8,  -3,   6, -32,
        -20,   0,  26,  11,  10,   3, -10, -32,
        -25,   9,   5,   9,  -4,  11, -34, -36,
        -48, -34, -18, -11, -16, -30, -37, -40,
        -15, -55, -37, -29, -36, -32, -56, -70,
    ];

    private static readonly int[] BishopMg =
    [
        -15,  18, -68, -23, -11, -28,  -3,   2,
        -12,  10,  -4,   1,  28,  45,   4, -33,
         -2,  47,  57,  38,  49,  64,  37,   8,
         10,  19,  21,  64,  31,  51,  11,  12,
          8,  17,  15,  36,  44,   2,  24,   4,
         14,  29,  21,  15,  12,  33,  16,  22,
         -6,  29,  30,   6,   9,  35,  41,  13,
        -19, -17,  -2, -29, -17,  -8, -25,  -7,
    ];

    private static readonly int[] BishopEg =
    [
        -28, -35, -17,   6, -13, -15, -31, -38,
        -22, -18,   1,  -2, -15, -11,   0, -28,
          4,   6, -14,   7,  -6,  20,  14,  10,
         -9,  15,  26,   7,  12,  -4,  17, -12,
          4,   3,  17,  15,   9,  10,  -9, -11,
        -18,  -9,  14,   8,  19,   1, -21, -17,
        -28, -12, -21, -15, -10, -19, -29, -41,
        -37, -15, -37, -19, -23, -30, -19, -31,
    ];

    private static readonly int[] RookMg =
    [
         46,  56,  38,  61,  53,  21,  45,  53,
         25,  30,  44,  52,  74,  57,  14,  34,
          9,  31,  28,  50,  31,  59,  55,  30,
        -10,   3,   9,  28,  38,  21,   6,  -6,
        -22, -20,   2,   3,  -5,   7,  -8, -21,
        -31, -19, -30, -29,  -3, -14, -11, -39,
        -38, -18, -26, -11,   5,  -3,  -8, -81,
         -9,  -5,  -1,  13,  20,  15, -41, -20,
    ];

    private static readonly int[] RookEg =
    [
          1,   0,  16,  17,   2,  22,  14,   3,
         -1,   1,  -1,  -3, -15, -11,  -6, -11,
         21,  21,  21,  15,  18,  11,   9,   3,
         18,  17,  27,  11,  16,  15,  13,  16,
         17,  19,  14,   6,   9,   4,   6,   1,
          8,   8,   9,   5, -17,  -6,  -6,  -4,
        -10, -12,  -4,  -4, -23,  -5, -19, -17,
          5,   2,  -1,  -3, -11,  -9,  18,  -8,
    ];

    private static readonly int[] QueenMg =
    [
        -14, -14,  15,  26,  45,  56,  29,  31,
        -14, -25,   1,  15,  -2,  43,  14,  42,
        -11,  -3,  19,  22,  35,  52,  33,  47,
        -17, -13,  -2,  -2,  11,  19,  -4,  13,
          1, -18,   5,   0,  10,  -4,   1,  -1,
        -18,   4,   1,   0,  -7,   0,   2,  -3,
        -23,   6,  17,   8,  20,  23,   9, -13,
         -1,  -6,   5,   2,  -1, -39, -45, -52,
    ];

    private static readonly int[] QueenEg =
    [
         -1,  14,   8,  19,  13,  13,  -4,   6,
         -3,  34,  46,  55,  72,  23,  16,  10,
         -6,  20,  23,  63,  45,  45,  13,  11,
         13,  36,  38,  59,  59,  54,  59,  38,
         -6,  40,  19,  43,  33,  48,  41,  29,
         -8, -13,   3,  14,  11,  15,  24,   9,
        -20, -37, -34, -24, -22, -37, -50, -46,
        -47, -42, -36, -45, -19, -46, -34, -33,
    ];

    private static readonly int[] KingMg =
    [
        -79,  37,  30,  -1, -42, -20,  16,  27,
         43,  13,  -6,   7,   6,  10, -24, -15,
          5,  38,  16,  -2, -12,  -8,   8,  -8,
         -3,  -6, -10, -39, -44, -37,  -4, -30,
        -47,   9, -21, -45, -58, -46, -23, -45,
        -28,  -2,  -8, -34, -30, -16,  -9, -41,
        -13,  -7,  -4, -50, -29, -12,  23,  22,
        -29,  22,   0, -52,  -6, -28,  38,  24,
    ];

    private static readonly int[] KingEg =
    [
        -60, -21,  -4,  -4,   3,  29,  18,  -3,
          2,  31,  28,  31,  31,  52,  37,  25,
         24,  31,  37,  29,  34,  31,  30,  27,
          6,  36,  34,  13,  12,  19,  24,  17,
         -4,  10,   7,  10,  13,   9,   7,  -1,
         -9,   3,  -3,   7,   9,   2,   1, -19,
        -17,  -1,   4,  11,   8,   2, -13, -27,
        -39, -32, -11,  -5, -38, -24, -30, -57,
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
