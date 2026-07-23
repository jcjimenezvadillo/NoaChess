using System.Runtime.Intrinsics.X86;

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
        public ulong Mask;          // Relevant blocker squares for this square.
        public ulong Magic;         // The multiplier.
        public int Shift;           // 64 - popcount(Mask).
        public ulong[] Attacks;     // Attack table indexed by the magic product.
        public ulong[]? PextAttacks; // Attack table indexed by PEXT(occ, Mask); null without BMI2.
    }

    private static readonly MagicEntry[] RookTable = new MagicEntry[64];
    private static readonly MagicEntry[] BishopTable = new MagicEntry[64];

    // PEXT replaces the whole mask-multiply-shift dance with one instruction —
    // but only where that instruction is actually fast. On AMD Zen1/Zen+/Zen2
    // (family 0x17) PEXT is microcoded (~18 cycles vs ~3) and LOSES to the
    // magic lookup, so those CPUs keep the magic path. The static readonly
    // bool is constant-folded by the JIT after class init: the untaken branch
    // costs nothing in either mode.
    public static readonly bool UsePext = ComputeUsePext();

    private static bool ComputeUsePext()
    {
        if (!Bmi2.X64.IsSupported || !X86Base.IsSupported)
            return false;

        (_, int ebx, int ecx, int edx) = X86Base.CpuId(0, 0);
        bool isAmd = ebx == 0x68747541 && edx == 0x69746E65 && ecx == 0x444D4163; // "AuthenticAMD"
        if (!isAmd)
            return true; // Intel (and others exposing BMI2) have fast PEXT.

        (int eax, _, _, _) = X86Base.CpuId(1, 0);
        int baseFamily = (eax >> 8) & 0xF;
        int extFamily = (eax >> 20) & 0xFF;
        int family = baseFamily == 0xF ? baseFamily + extFamily : baseFamily;
        return family >= 0x19; // Zen3 and newer.
    }

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
        if (UsePext)
            return e.PextAttacks![(int)Bmi2.X64.ParallelBitExtract(occupancy, e.Mask)];
        return e.Attacks[((occupancy & e.Mask) * e.Magic) >> e.Shift];
    }

    public static ulong BishopAttacks(int square, ulong occupancy)
    {
        ref MagicEntry e = ref BishopTable[square];
        if (UsePext)
            return e.PextAttacks![(int)Bmi2.X64.ParallelBitExtract(occupancy, e.Mask)];
        return e.Attacks[((occupancy & e.Mask) * e.Magic) >> e.Shift];
    }

    // Explicit single-path lookups so tests can cross-validate BOTH paths on
    // any BMI2 machine — a Zen+/Zen2 box never takes the PEXT branch in
    // production (UsePext is false there), yet must still verify it.
    public static bool PextTablesBuilt => RookTable[0].PextAttacks is not null;

    public static ulong RookAttacksPext(int square, ulong occupancy)
    {
        ref MagicEntry e = ref RookTable[square];
        return e.PextAttacks![(int)Bmi2.X64.ParallelBitExtract(occupancy, e.Mask)];
    }

    public static ulong BishopAttacksPext(int square, ulong occupancy)
    {
        ref MagicEntry e = ref BishopTable[square];
        return e.PextAttacks![(int)Bmi2.X64.ParallelBitExtract(occupancy, e.Mask)];
    }

    public static ulong RookAttacksMagic(int square, ulong occupancy)
    {
        ref MagicEntry e = ref RookTable[square];
        return e.Attacks[((occupancy & e.Mask) * e.Magic) >> e.Shift];
    }

    public static ulong BishopAttacksMagic(int square, ulong occupancy)
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

                // PEXT layout of the same data: the extracted mask bits ARE the
                // index, no magic needed. Built whenever the CPU has BMI2 so
                // the tests can validate the path even where UsePext is false.
                ulong[]? pextAttacks = null;
                if (Bmi2.X64.IsSupported)
                {
                    pextAttacks = new ulong[tableSize];
                    for (int i = 0; i < tableSize; i++)
                        pextAttacks[(int)Bmi2.X64.ParallelBitExtract(subsets[i], mask)] = reference[i];
                }

                return new MagicEntry
                {
                    Mask = mask, Magic = magic, Shift = shift,
                    Attacks = attacks, PextAttacks = pextAttacks,
                };
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
