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
// which - shifted down - form a perfect index into a precomputed attack table:
//
//   attacks = table[((occupancy & mask) * magic) >> shift]
//
// The magic constants were FOUND, not designed: random sparse 64-bit numbers
// were tried until one mapped every possible blocker subset to a distinct
// table slot (or to slots that happen to share identical attack sets, which
// is harmless). The search (a fixed-seed xorshift, so deterministic) used to
// run at every startup and cost over two seconds of CPU per process - the
// lichess bot starts a fresh engine for every game - so its 128 results are
// stored below and the tables are filled straight from them, byte for byte
// the same tables as before. A constant that mapped two subsets with
// different attacks to one slot would throw while the table is filled.
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

    // PEXT replaces the whole mask-multiply-shift dance with one instruction -
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

    private static readonly ulong[] RookMagics =
    [
        0x0980008011400020UL, 0x8340004410002000UL, 0x0880200090008268UL, 0x0080080080100004UL,
        0x8100110004020800UL, 0x0300010004000822UL, 0x08801A0029000080UL, 0x8100050001204882UL,
        0x0844800081400320UL, 0x0804402010004000UL, 0x0108802003100480UL, 0x5C00808010000800UL,
        0x0003001801001014UL, 0x0002000200041008UL, 0x0004008108042210UL, 0x0105000100009042UL,
        0x0400808000400021UL, 0xC100404010002000UL, 0x0060008010002088UL, 0x0400808008001000UL,
        0x4440808008000400UL, 0x1002008004000280UL, 0x40024400300D1248UL, 0x0010020000408104UL,
        0x0101008200204200UL, 0x8020002040005000UL, 0x4100100080802000UL, 0x4008006A80100280UL,
        0x0021008500100800UL, 0x5000040080800200UL, 0x0010040101000200UL, 0x6140004200008104UL,
        0x4000400020800090UL, 0x2020002080804000UL, 0x0000408202002010UL, 0x0080100501000820UL,
        0x0004040080800800UL, 0xA01A800200800400UL, 0x0014820104009008UL, 0x0008004082000401UL,
        0x0029804004608010UL, 0x0140002000818048UL, 0x000C410420010014UL, 0x0020100008008080UL,
        0x8000080004008080UL, 0x0082008004008002UL, 0x1002000801020004UL, 0x0010848505620004UL,
        0x0000801840002080UL, 0x2002010C80403200UL, 0x9000114220010300UL, 0x0001000820100100UL,
        0x000A800401080080UL, 0xC001400410200801UL, 0x4800480102300400UL, 0x1000010040840200UL,
        0x0002800442162101UL, 0x4000810010204202UL, 0x0400201200084082UL, 0x8200210004081001UL,
        0x1002001004200802UL, 0x0005000208040001UL, 0x0002002701AC0822UL, 0x000010250184004AUL,
    ];

    private static readonly ulong[] BishopMagics =
    [
        0x4014700208012480UL, 0x0220014200A10001UL, 0x8108084040806120UL, 0x200E0A02001A00C0UL,
        0x4442021080243000UL, 0x4C00901008100050UL, 0x0800821110404840UL, 0x0000120222024003UL,
        0x008A08A041020200UL, 0x000118460C0428A0UL, 0x0000314B06020022UL, 0x0804180481080025UL,
        0x0000340520004880UL, 0x0440309004200008UL, 0x2108084402A01000UL, 0x0328024422082200UL,
        0xC21200C004310C00UL, 0x460840040820C400UL, 0x0010004A02720020UL, 0x000800488A044044UL,
        0x0025000A9040000AUL, 0x0009010600822104UL, 0x0021040400821002UL, 0x0801000A19010120UL,
        0x0004048210915000UL, 0x0310222005242C05UL, 0x00544800101C8010UL, 0x2308080000820003UL,
        0x2008820004010401UL, 0x0650110244208800UL, 0x0009324401080802UL, 0x01008022A6010C40UL,
        0x8081200804200940UL, 0x300C040305200240UL, 0x0002062200240800UL, 0x2801400A00042200UL,
        0x14090104000202A0UL, 0x05B0020080081048UL, 0x0A10010910020880UL, 0xC403020222060B00UL,
        0x260D301250402080UL, 0x0000A80802088800UL, 0x0001008041023000UL, 0x0000020202016420UL,
        0x0008540502140400UL, 0x0201101008400B80UL, 0x0104104202000058UL, 0x0002080101000031UL,
        0x4200A20120200004UL, 0x0004208410090400UL, 0x10A0182508088000UL, 0x1814081420880800UL,
        0x1008810850340611UL, 0x0081482248020610UL, 0x0009080800940000UL, 0x0060010240890800UL,
        0x048A920802080500UL, 0x26000E9204900404UL, 0x200002A080482200UL, 0xAC8018422020A808UL,
        0x929A000440A50308UL, 0x41801040826C0110UL, 0x0140C01081010101UL, 0xC0A0012206040EA0UL,
    ];

    static Magics()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            RookTable[sq] = BuildEntry(sq, RookDirections, RookMagics[sq]);
            BishopTable[sq] = BuildEntry(sq, BishopDirections, BishopMagics[sq]);
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
    // any BMI2 machine - a Zen+/Zen2 box never takes the PEXT branch in
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

    private static MagicEntry BuildEntry(int square, (int df, int dr)[] directions, ulong magic)
    {
        // Relevant mask: ray squares EXCLUDING the board edge - a blocker on
        // the last square of a ray changes nothing (the ray ends there anyway),
        // so edge squares would only waste table space.
        ulong mask = RelevantMask(square, directions);
        int bits = Bitboard.PopCount(mask);
        int shift = 64 - bits;
        int tableSize = 1 << bits;

        // Enumerate every blocker subset of the mask (Carry-Rippler trick) and
        // precompute its true attack set by ray walking (the slow reference
        // implementation is only used here, at startup).
        var attacks = new ulong[tableSize];
        var filled = new bool[tableSize];
        ulong[]? pextAttacks = Bmi2.X64.IsSupported ? new ulong[tableSize] : null;
        ulong subset = 0;
        for (int i = 0; i < tableSize; i++)
        {
            ulong reference = RayAttacks(square, subset, directions);
            int index = (int)((subset * magic) >> shift);
            if (filled[index] && attacks[index] != reference)
                throw new InvalidOperationException($"Magic 0x{magic:X16} collides on square {square}.");
            filled[index] = true;
            attacks[index] = reference;

            // PEXT layout of the same data: the extracted mask bits ARE the
            // index, no magic needed. Built whenever the CPU has BMI2 so
            // the tests can validate the path even where UsePext is false.
            if (pextAttacks is not null)
                pextAttacks[(int)Bmi2.X64.ParallelBitExtract(subset, mask)] = reference;

            subset = (subset - mask) & mask; // Next subset of 'mask'.
        }

        return new MagicEntry
        {
            Mask = mask, Magic = magic, Shift = shift,
            Attacks = attacks, PextAttacks = pextAttacks,
        };
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
}
