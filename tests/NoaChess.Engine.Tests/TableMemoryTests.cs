using NoaChess.Core;
using NoaChess.Engine.Search;
using NoaChess.Engine.Transposition;

namespace NoaChess.Engine.Tests;

// The transposition table's storage: large pages when the OS grants them,
// the pinned array otherwise. Either way the table must behave identically,
// survive a resize that keeps the old block for a late reader, and clear.
public class TableMemoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoreProbeResizeAndClearWorkInBothModes(bool largePages)
    {
        bool saved = TranspositionTable.LargePagesAllowed;
        try
        {
            TranspositionTable.LargePagesAllowed = largePages;
            var tt = new TranspositionTable(sizeMb: 16);
            Assert.Equal(16, tt.SizeMb);
            if (!largePages)
                Assert.False(tt.UsesLargePages);

            var move = new Move(12, 28, MoveFlag.Quiet);
            tt.Store(0x1234_5678_9ABC_DEF0UL, depth: 7, score: 42, staticEval: 30,
                     BoundType.Exact, move, isPv: true);
            Assert.True(tt.Probe(0x1234_5678_9ABC_DEF0UL, out TTEntry entry));
            Assert.Equal(42, entry.Score);
            Assert.Equal(move, entry.BestMove);

            // A resize that keeps the old block alive (a late reader) still
            // hands out a fresh, empty table.
            tt.Resize(sizeMb: 8, releaseOld: false);
            Assert.Equal(8, tt.SizeMb);
            Assert.False(tt.Probe(0x1234_5678_9ABC_DEF0UL, out _));
            tt.Store(0x0FED_CBA9_8765_4321UL, 3, -5, -5, BoundType.LowerBound, move, isPv: false);
            Assert.True(tt.Probe(0x0FED_CBA9_8765_4321UL, out _));

            tt.Clear();
            Assert.False(tt.Probe(0x0FED_CBA9_8765_4321UL, out _));
        }
        finally
        {
            TranspositionTable.LargePagesAllowed = saved;
        }
    }

    [Fact]
    public void SearchIsNodeIdenticalWithLargePagesAllowed()
    {
        var board = new Board("r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4");
        long Nodes(bool largePages)
        {
            bool saved = TranspositionTable.LargePagesAllowed;
            try
            {
                TranspositionTable.LargePagesAllowed = largePages;
                var engine = new ChessEngine();
                engine.ResizeHash(32);
                return engine.FindBestMove(board.Clone(), SearchLimits.Depth(9)).NodesSearched;
            }
            finally
            {
                TranspositionTable.LargePagesAllowed = saved;
            }
        }
        Assert.Equal(Nodes(false), Nodes(true));
    }
}
