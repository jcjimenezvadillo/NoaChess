using NoaChess.Core;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Tests;

// Unit tests of the clustered transposition table's storage semantics (5F).
public class TranspositionTableTests
{
    private static TranspositionTable NewTable()
    {
        var tt = new TranspositionTable(sizeMb: 1);
        tt.NewSearch(); // Generation 1: entries distinguishable from empty slots.
        return tt;
    }

    [Fact]
    public void StoreAndProbe_RoundTrips()
    {
        var tt = NewTable();
        var move = new Move(12, 28, MoveFlag.Quiet); // e2e4

        tt.Store(0xDEADBEEF12345678UL, depth: 5, score: 42, staticEval: 17,
                 BoundType.Exact, move, isPv: true);

        Assert.True(tt.Probe(0xDEADBEEF12345678UL, out TTEntry entry));
        Assert.Equal(5, entry.Depth);
        Assert.Equal(42, entry.Score);
        Assert.Equal(17, entry.StaticEval);
        Assert.Equal(BoundType.Exact, entry.Bound);
        Assert.Equal(move, entry.BestMove);
        Assert.True(entry.IsPv);
    }

    [Fact]
    public void Probe_MissesUnknownKey()
    {
        var tt = NewTable();
        Assert.False(tt.Probe(0x123456789ABCDEFUL, out _));
    }

    [Fact]
    public void Store_ShallowBoundDoesNotEvictDeepResult()
    {
        var tt = NewTable();
        ulong key = 0xCAFE00000000CAFEUL;

        tt.Store(key, depth: 8, score: 100, staticEval: 0, BoundType.Exact, Move.None, false);
        // A much shallower BOUND for the same position must NOT evict it.
        tt.Store(key, depth: 3, score: -50, staticEval: 0, BoundType.LowerBound, Move.None, false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(8, entry.Depth);
        Assert.Equal(100, entry.Score);
    }

    [Fact]
    public void Store_FreshExactAlwaysOverwrites()
    {
        var tt = NewTable();
        ulong key = 0xCAFE00000000CAFEUL;

        tt.Store(key, depth: 8, score: 100, staticEval: 0, BoundType.LowerBound, Move.None, false);
        // The reference rule: a full-window exact value replaces any bound.
        tt.Store(key, depth: 3, score: -50, staticEval: 0, BoundType.Exact, Move.None, false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(3, entry.Depth);
        Assert.Equal(-50, entry.Score);
    }

    [Fact]
    public void Store_KeepsBestMoveWhenNewResultHasNone()
    {
        var tt = NewTable();
        ulong key = 0xABCD0000ABCD0000UL;
        var move = new Move(12, 28, MoveFlag.Quiet);

        tt.Store(key, depth: 5, score: 10, staticEval: 0, BoundType.Exact, move, false);
        tt.Store(key, depth: 6, score: 20, staticEval: 0, BoundType.Exact, Move.None, false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(move, entry.BestMove);
        Assert.Equal(20, entry.Score);
    }

    [Fact]
    public void EvalOnlyEntry_CachesEvalButNeverCuts()
    {
        var tt = NewTable();
        ulong key = 0x1111222233334444UL;

        tt.Store(key, depth: 0, score: 0, staticEval: 123,
                 BoundType.None, Move.None, false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(123, entry.StaticEval);
        Assert.Equal(BoundType.None, entry.Bound);
        Assert.Equal(Move.None, entry.BestMove);
    }

    [Fact]
    public void EvalOnlyRefresh_DoesNotClobberSearchResult()
    {
        var tt = NewTable();
        ulong key = 0x5555666677778888UL;
        var move = new Move(12, 28, MoveFlag.Quiet);

        tt.Store(key, depth: 9, score: 77, staticEval: 30, BoundType.Exact, move, true);
        tt.Store(key, depth: 0, score: 0, staticEval: 99, BoundType.None, Move.None, false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(9, entry.Depth);
        Assert.Equal(77, entry.Score);
        Assert.Equal(30, entry.StaticEval); // Existing eval survives.
        Assert.Equal(move, entry.BestMove);
    }

    [Fact]
    public void PvFlag_SticksAcrossRestores()
    {
        var tt = NewTable();
        ulong key = 0x9999AAAABBBBCCCCUL;

        tt.Store(key, depth: 6, score: 5, staticEval: 0, BoundType.Exact, Move.None, isPv: true);
        // A later non-PV visit must not strip the mark.
        tt.Store(key, depth: 7, score: 8, staticEval: 0, BoundType.Exact, Move.None, isPv: false);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.True(entry.IsPv);
    }

    [Fact]
    public void Cluster_HoldsFourPositionsWithSameIndexBits()
    {
        var tt = NewTable();
        // 1 MB => 16384 clusters: keys differing only above bit 14 share one.
        ulong baseKey = 0x42UL;
        for (ulong i = 0; i < 4; i++)
            tt.Store(baseKey | ((0x1000 + i) << 32), depth: 5, score: (int)i,
                     staticEval: 0, BoundType.Exact, Move.None, false);

        for (ulong i = 0; i < 4; i++)
        {
            Assert.True(tt.Probe(baseKey | ((0x1000 + i) << 32), out TTEntry e));
            Assert.Equal((int)i, e.Score);
        }
    }

    [Fact]
    public void Aging_OldShallowEntryYieldsBeforeFreshDeepOne()
    {
        var tt = NewTable();
        ulong baseKey = 0x77UL;

        // Fill the cluster: one deep fresh entry and three shallow ones.
        tt.Store(baseKey | (0x100UL << 32), 12, 1, 0, BoundType.Exact, Move.None, false);
        tt.Store(baseKey | (0x200UL << 32), 3, 2, 0, BoundType.Exact, Move.None, false);
        tt.Store(baseKey | (0x300UL << 32), 3, 3, 0, BoundType.Exact, Move.None, false);
        tt.Store(baseKey | (0x400UL << 32), 3, 4, 0, BoundType.Exact, Move.None, false);

        // Several searches later, a fifth position needs a slot: a shallow
        // old entry is evicted, the deep one survives.
        for (int i = 0; i < 4; i++)
            tt.NewSearch();
        tt.Store(baseKey | (0x500UL << 32), 5, 5, 0, BoundType.Exact, Move.None, false);

        Assert.True(tt.Probe(baseKey | (0x100UL << 32), out TTEntry deep));
        Assert.Equal(12, deep.Depth);
        Assert.True(tt.Probe(baseKey | (0x500UL << 32), out _));
    }

    [Fact]
    public void Clear_EmptiesTheTable()
    {
        var tt = NewTable();
        tt.Store(0xCAFE0000CAFE0000UL, 5, 42, 0, BoundType.Exact, Move.None, false);

        tt.Clear();

        Assert.False(tt.Probe(0xCAFE0000CAFE0000UL, out _));
    }
}
