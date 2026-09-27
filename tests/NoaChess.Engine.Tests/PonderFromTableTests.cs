using NoaChess.Core;
using NoaChess.Engine;
using NoaChess.Engine.Search;

namespace NoaChess.Engine.Tests;

// The ponder hint falls back to the move the shared table stores after the
// move played when that move is not the head of the printed PV (Lazy SMP
// vote, the pondered-move rule). The PV itself is rebuilt from the same table,
// so right after a search the table move of the child position is the PV's
// second move, and it is legal there.
public class PonderFromTableTests
{
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4")]
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1")]
    public void TableMoveAfterBestMoveIsTheLegalSecondPvMove(string fen)
    {
        var engine = new ChessEngine();
        var board = new Board(fen);
        Move[] pv = [];
        var progress = new SyncProgress(p => pv = p.Pv);

        SearchResult result = engine.FindBestMove(board, SearchLimits.Depth(9), default, progress);

        Assert.True(pv.Length >= 2, "the search must print a PV with a reply");
        Assert.Equal(result.BestMove, pv[0]);
        board.MakeMove(result.BestMove);
        Move tableMove = engine.TableMove(board);
        Assert.Contains(tableMove, MoveGenerator.GenerateLegalMoves(board));
        Assert.Equal(pv[1], tableMove);
    }

    private sealed class SyncProgress(Action<SearchProgress> report) : IProgress<SearchProgress>
    {
        public void Report(SearchProgress value) => report(value);
    }
}
