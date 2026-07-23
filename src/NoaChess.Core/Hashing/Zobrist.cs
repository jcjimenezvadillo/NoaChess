namespace NoaChess.Core;

// Zobrist keys for position hashing.
//
// Idea: every "fact" of the position (piece X on square Y, black to move,
// each castling right, en passant file) gets a fixed random 64-bit number.
// The hash of a position is the XOR of the keys of all its facts.
//
// The beauty of XOR is that it is its own inverse: when a piece moves, it is
// enough to XOR "piece on origin" (removes it) and XOR "piece on destination"
// (adds it), without recomputing the whole hash. This allows keeping the hash
// updated incrementally in MakeMove/UnmakeMove, and is the foundation of the
// future transposition table.
public static class Zobrist
{
    // [color][pieceType][square]
    public static readonly ulong[,,] PieceKeys = new ulong[2, 6, 64];

    // Applied when it is black's turn to move.
    public static readonly ulong SideToMoveKey;

    // One key per combination of castling rights (16 possible combinations).
    public static readonly ulong[] CastlingKeys = new ulong[16];

    // One key per file of the en passant square (the rank can be deduced from the turn).
    public static readonly ulong[] EnPassantFileKeys = new ulong[8];

    static Zobrist()
    {
        // Xorshift generator with a FIXED seed: the keys must be identical on
        // every run so tests are deterministic and, in the future, hashes are
        // comparable across sessions.
        ulong state = 0x9E3779B97F4A7C15UL;
        ulong Next()
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return state;
        }

        for (int c = 0; c < 2; c++)
            for (int p = 0; p < 6; p++)
                for (int s = 0; s < 64; s++)
                    PieceKeys[c, p, s] = Next();

        SideToMoveKey = Next();

        for (int i = 0; i < 16; i++)
            CastlingKeys[i] = Next();

        for (int i = 0; i < 8; i++)
            EnPassantFileKeys[i] = Next();
    }
}
