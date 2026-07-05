using NoaChess.Core;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Tests;

// Unit tests of the transposition table's storage semantics.
public class TranspositionTableTests
{
    [Fact]
    public void StoreAndProbe_RoundTrips()
    {
        var tt = new TranspositionTable(sizeMb: 1);
        var move = new Move(12, 28, MoveFlag.Quiet); // e2e4

        tt.Store(0xDEADBEEFUL, depth: 5, score: 42, BoundType.Exact, move);

        Assert.True(tt.Probe(0xDEADBEEFUL, out TTEntry entry));
        Assert.Equal(5, entry.Depth);
        Assert.Equal(42, entry.Score);
        Assert.Equal(BoundType.Exact, entry.Bound);
        Assert.Equal(move, entry.BestMove);
    }

    [Fact]
    public void Probe_MissesUnknownKey()
    {
        var tt = new TranspositionTable(sizeMb: 1);
        Assert.False(tt.Probe(0x123456UL, out _));
    }

    [Fact]
    public void Store_PrefersDeeperSearch()
    {
        var tt = new TranspositionTable(sizeMb: 1);
        ulong key = 0xCAFEUL;

        tt.Store(key, depth: 8, score: 100, BoundType.Exact, Move.None);
        // A shallower result for the same position must NOT evict the deep one.
        tt.Store(key, depth: 3, score: -50, BoundType.Exact, Move.None);

        Assert.True(tt.Probe(key, out TTEntry entry));
        Assert.Equal(8, entry.Depth);
        Assert.Equal(100, entry.Score);
    }

    [Fact]
    public void Clear_EmptiesTheTable()
    {
        var tt = new TranspositionTable(sizeMb: 1);
        tt.Store(0xCAFEUL, 5, 42, BoundType.Exact, Move.None);

        tt.Clear();

        Assert.False(tt.Probe(0xCAFEUL, out _));
    }
}
