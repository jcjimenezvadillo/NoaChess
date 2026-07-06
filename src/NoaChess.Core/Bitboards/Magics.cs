namespace NoaChess.Core;

// Magic bitboards: O(1) sliding-piece attack lookup.
//
// Problem: rook/bishop attacks depend on which squares are occupied. Walking
// the rays on every call (v0.1-v1.0) costs dozens of branches per lookup, and
// slider attacks are THE hot spot of move generation and IsSquareAttacked.
//
// The trick: for each square, all occupancy bits that can possibly block the
// rays are collected in a "relevant mask" (up to 12 bits for rooks, 9 for
// bishops). Multiplying the masked occupancy by a carefully chosen "magic"
// constant makes the relevant bits collide into the TOP bits of the product,
// which — shifted down — form a perfect index into a precomputed attack table:
//
//   attacks = table[((occupancy & mask) * magic) >> shift]
//
// The magic constants are FOUND, not designed: random sparse 64-bit numbers
// are tried until one maps every possible blocker subset to a distinct table
// slot (or to slots that happen to share identical attack sets, which is
// harmless). With a fixed RNG seed the search is deterministic and takes a
// few tens of milliseconds at startup — cheaper to regenerate than to
// maintain 128 hardcoded constants.
public static class Magics
{
    private struct MagicEntry
    {
        public ulong Mask;      // Relevant blocker squares for this square.
        public ulong Magic;     // The multiplier.
        public int Shift;       // 64 - popcount(Mask).
        public ulong[] Attacks; // Attack table indexed by the magic product.
    }

    private static readonly MagicEntry[] RookTable = new MagicEntry[64];
    private static readonly MagicEntry[] BishopTable = new MagicEntry[64];

    private static readonly (int df, int dr)[] RookDirections = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int df, int dr)[] BishopDirections = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    static Magics()
    {
        // Deterministic RNG (fixed seed): same magics on every run.
        ulong rngState = 0x2545F4914F6CDD1DUL;

        for (int sq = 0; sq < 64; sq++)
        {
            RookTable[sq] = BuildEntry(sq, RookDirections, ref rngState);
            BishopTable[sq] = BuildEntry(sq, BishopDirections, ref rngState);
        }
    }

    public static ulong RookAttacks(int square, ulong occupancy)
    {
        ref MagicEntry e = ref RookTable[square];
        return e.Attacks[((occupancy & e.Mask) * e.Magic) >> e.Shift];
    }

    public static ulong BishopAttacks(int square, ulong occupancy)
    {
        ref MagicEntry e = ref BishopTable[square];
        return e.Attacks[((occupancy & e.Mask) * e.Magic) >> e.Shift];
    }

    private static MagicEntry BuildEntry(int square, (int df, int dr)[] directions, ref ulong rngState)
    {
        // Relevant mask: ray squares EXCLUDING the board edge — a blocker on
        // the last square of a ray changes nothing (the ray ends there anyway),
        // so edge squares would only waste table space.
        ulong mask = RelevantMask(square, directions);
        int bits = Bitboard.PopCount(mask);
        int shift = 64 - bits;
        int tableSize = 1 << bits;

        // Enumerate every blocker subset of the mask (Carry-Rippler trick) and
        // precompute its true attack set by ray walking (the slow reference
        // implementation is only used here, at startup).
        var subsets = new ulong[tableSize];
        var reference = new ulong[tableSize];
        ulong subset = 0;
        for (int i = 0; i < tableSize; i++)
        {
            subsets[i] = subset;
            reference[i] = RayAttacks(square, subset, directions);
            subset = (subset - mask) & mask; // Next subset of 'mask'.
        }

        // Try random sparse candidates until one is collision-free.
        var attacks = new ulong[tableSize];
        while (true)
        {
            // Sparse random (AND of three xorshifts): good magics are sparse.
            ulong magic = NextRandom(ref rngState) & NextRandom(ref rngState) & NextRandom(ref rngState);

            // Quick rejection: the magic must spread the mask's high bits.
            if (Bitboard.PopCount((mask * magic) >> 56) < 6)
                continue;

            Array.Clear(attacks);
            bool collision = false;

            for (int i = 0; i < tableSize && !collision; i++)
            {
                int index = (int)((subsets[i] * magic) >> shift);
                if (attacks[index] == 0)
                    attacks[index] = reference[i];
                else if (attacks[index] != reference[i])
                    collision = true; // Two subsets, different attacks: bad magic.
            }

            if (!collision)
            {
                // Slot 0 may legitimately hold an empty-looking attack set; the
                // check above used 0 as "empty" marker. Re-fill to be exact.
                Array.Clear(attacks);
                for (int i = 0; i < tableSize; i++)
                    attacks[(int)((subsets[i] * magic) >> shift)] = reference[i];

                return new MagicEntry { Mask = mask, Magic = magic, Shift = shift, Attacks = attacks };
            }
        }
    }

    private static ulong RelevantMask(int square, (int df, int dr)[] directions)
    {
        ulong mask = 0;
        int f0 = Squares.FileOf(square), r0 = Squares.RankOf(square);
        foreach ((int df, int dr) in directions)
        {
            int f = f0 + df, r = r0 + dr;
            // Stop BEFORE the edge: edge blockers are irrelevant.
            while (f + df is >= 0 and <= 7 && r + dr is >= 0 and <= 7 &&
                   f is >= 0 and <= 7 && r is >= 0 and <= 7)
            {
                mask |= Bitboard.SquareBB(Squares.FromFileRank(f, r));
                f += df;
                r += dr;
            }
        }
        return mask;
    }

    // Reference ray walker (same semantics as the pre-magic implementation).
    private static ulong RayAttacks(int square, ulong occupancy, (int df, int dr)[] directions)
    {
        ulong attacks = 0;
        int f0 = Squares.FileOf(square), r0 = Squares.RankOf(square);
        foreach ((int df, int dr) in directions)
        {
            int f = f0 + df, r = r0 + dr;
            while (f is >= 0 and <= 7 && r is >= 0 and <= 7)
            {
                int sq = Squares.FromFileRank(f, r);
                attacks |= Bitboard.SquareBB(sq);
                if (Bitboard.IsSet(occupancy, sq))
                    break;
                f += df;
                r += dr;
            }
        }
        return attacks;
    }

    private static ulong NextRandom(ref ulong state)
    {
        state ^= state << 13;
        state ^= state >> 7;
        state ^= state << 17;
        return state;
    }
}
