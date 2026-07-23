using NoaChess.Core;

namespace NoaChess.Core.Tests;

// The PEXT and magic lookup paths must agree on every square and occupancy.
// Production picks one path per CPU (Magics.UsePext — PEXT only on Intel/AMD
// Zen3+, where the instruction is fast); this test exercises BOTH explicitly,
// so the PEXT path is verified even on machines where production never takes
// it (e.g. AMD Zen+/Zen2, where PEXT is microcoded and the guard disables it).
public class MagicsPextTests
{
    [Fact]
    public void PextAndMagicLookupsAgree()
    {
        if (!Magics.PextTablesBuilt)
            return; // No BMI2 on this machine: nothing to validate.

        var rng = new Random(42);
        for (int sq = 0; sq < 64; sq++)
        {
            // Empty and full boards plus a batch of random occupancies.
            foreach (ulong occ in RandomOccupancies(rng, 500))
            {
                Assert.Equal(Magics.RookAttacksMagic(sq, occ), Magics.RookAttacksPext(sq, occ));
                Assert.Equal(Magics.BishopAttacksMagic(sq, occ), Magics.BishopAttacksPext(sq, occ));
            }
        }
    }

    private static IEnumerable<ulong> RandomOccupancies(Random rng, int count)
    {
        yield return 0UL;
        yield return ulong.MaxValue;
        for (int i = 0; i < count; i++)
        {
            // AND-ing randoms gives sparse boards, OR-ing gives dense ones —
            // both regimes are exercised.
            ulong a = (ulong)rng.NextInt64();
            ulong b = (ulong)rng.NextInt64();
            yield return i % 2 == 0 ? a & b : a | b;
        }
    }
}
