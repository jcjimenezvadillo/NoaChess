namespace NoaChess.Core;

// Precomputed attack tables for every piece type.
//
// For "jumping" pieces (knight, king) and pawns, the attacks from each square
// are fixed and are precomputed once in the static constructor.
//
// For sliding pieces (rook, bishop, queen) the attacks depend on the pieces
// blocking the path ("blockers"), so they are computed by walking rays on
// every call. NOTE: in future versions this will be replaced by Magic
// Bitboards (O(1) lookup per square), but for the MVP the ray scan is correct,
// easy to verify with Perft and fast enough.
public static class Attacks
{
    // Precomputed attacks: [square] -> bitboard of attacked squares.
    private static readonly ulong[] KnightAttacks = new ulong[64];
    private static readonly ulong[] KingAttacks = new ulong[64];

    // Pawn attacks depend on color: [color][square].
    // Note: these are the squares the pawn ATTACKS (diagonals), not where it advances.
    private static readonly ulong[][] PawnAttacksTable = [new ulong[64], new ulong[64]];

    // Ray directions as (deltaFile, deltaRank). They are walked square by
    // square until leaving the board or hitting a blocker.
    private static readonly (int df, int dr)[] RookDirections = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int df, int dr)[] BishopDirections = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    static Attacks()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            int f = Squares.FileOf(sq);
            int r = Squares.RankOf(sq);

            // The 8 possible knight jumps.
            foreach ((int df, int dr) in new[] { (1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2) })
                AddIfOnBoard(ref KnightAttacks[sq], f + df, r + dr);

            // The 8 squares adjacent to the king.
            for (int df = -1; df <= 1; df++)
                for (int dr = -1; dr <= 1; dr++)
                    if (df != 0 || dr != 0)
                        AddIfOnBoard(ref KingAttacks[sq], f + df, r + dr);

            // Diagonal pawn captures. The white pawn attacks "upwards" (+1 rank)
            // and the black one "downwards" (-1 rank).
            AddIfOnBoard(ref PawnAttacksTable[(int)Color.White][sq], f - 1, r + 1);
            AddIfOnBoard(ref PawnAttacksTable[(int)Color.White][sq], f + 1, r + 1);
            AddIfOnBoard(ref PawnAttacksTable[(int)Color.Black][sq], f - 1, r - 1);
            AddIfOnBoard(ref PawnAttacksTable[(int)Color.Black][sq], f + 1, r - 1);
        }
    }

    private static void AddIfOnBoard(ref ulong bb, int file, int rank)
    {
        if (file is >= 0 and <= 7 && rank is >= 0 and <= 7)
            bb |= Bitboard.SquareBB(Squares.FromFileRank(file, rank));
    }

    public static ulong Knight(int square) => KnightAttacks[square];
    public static ulong King(int square) => KingAttacks[square];
    public static ulong Pawn(Color color, int square) => PawnAttacksTable[(int)color][square];

    // Rook attacks from a square given the "occupancy" (every piece on the
    // board, friendly and enemy). The ray includes the first blocking piece:
    // if it is an enemy it will be a possible capture, if it is friendly it
    // gets filtered out later.
    public static ulong Rook(int square, ulong occupancy) => Slider(square, occupancy, RookDirections);

    // Bishop attacks (see Rook for the occupancy semantics).
    public static ulong Bishop(int square, ulong occupancy) => Slider(square, occupancy, BishopDirections);

    // The queen attacks as a rook and a bishop at the same time.
    public static ulong Queen(int square, ulong occupancy) =>
        Rook(square, occupancy) | Bishop(square, occupancy);

    private static ulong Slider(int square, ulong occupancy, (int df, int dr)[] directions)
    {
        ulong attacks = 0;
        int f0 = Squares.FileOf(square);
        int r0 = Squares.RankOf(square);

        foreach ((int df, int dr) in directions)
        {
            int f = f0 + df, r = r0 + dr;
            while (f is >= 0 and <= 7 && r is >= 0 and <= 7)
            {
                int sq = Squares.FromFileRank(f, r);
                attacks |= Bitboard.SquareBB(sq);
                // The ray stops when hitting any piece (the blocker's square IS
                // included: it may be a capture).
                if (Bitboard.IsSet(occupancy, sq))
                    break;
                f += df;
                r += dr;
            }
        }
        return attacks;
    }
}
