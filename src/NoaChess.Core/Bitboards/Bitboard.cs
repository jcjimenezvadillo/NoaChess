using System.Numerics;

namespace NoaChess.Core;

// Bitboard utilities. A bitboard is a 64-bit ulong where each bit represents
// one square of the board (bit 0 = a1 ... bit 63 = h8). It lets us operate on
// sets of squares with bit operations, which are extremely fast: for example,
// "all the squares attacked by the white knights" is a single ulong.
public static class Bitboard
{
    // File masks. They are used to prevent a bit shift from "wrapping around"
    // the board: e.g. a pawn on the 'a' file cannot capture to the left, so
    // FileA is masked out before shifting.
    public const ulong FileA = 0x0101010101010101UL;
    public const ulong FileH = 0x8080808080808080UL;
    public const ulong Rank1 = 0x00000000000000FFUL;
    public const ulong Rank8 = 0xFF00000000000000UL;

    // Bitboard with a single bit set on the given square.
    public static ulong SquareBB(int square) => 1UL << square;

    // True if the bit for the square is set.
    public static bool IsSet(ulong bb, int square) => (bb & (1UL << square)) != 0;

    // Number of set bits (number of squares in the set).
    public static int PopCount(ulong bb) => BitOperations.PopCount(bb);

    // Index of the least significant set bit (first square of the set).
    public static int Lsb(ulong bb) => BitOperations.TrailingZeroCount(bb);

    // Extracts and returns the first square of the bitboard, removing it from
    // the set. Typical iteration pattern:
    // while (bb != 0) { int sq = PopLsb(ref bb); ... }
    public static int PopLsb(ref ulong bb)
    {
        int sq = BitOperations.TrailingZeroCount(bb);
        bb &= bb - 1; // Classic trick: clears the lowest set bit.
        return sq;
    }
}
