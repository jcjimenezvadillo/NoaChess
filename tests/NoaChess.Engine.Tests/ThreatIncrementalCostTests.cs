using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// How much work the incremental threat update actually saves.
//
// WHY THIS AND NOT A STOPWATCH. Whether the incremental pays is ultimately an
// NPS question, and NPS needs an idle machine and paired runs - this project has
// already been burned comparing unpaired timings taken under different loads.
// Row operations do not care about load: the same walk touches the same rows
// every time, so this number is reproducible while the machine is busy with
// something else, and it is the quantity the design argument was actually made
// about ("about six row additions instead of about thirty-seven").
//
// It does NOT replace the timing. A row operation is a strided write over the
// transformer width and a refresh also walks the board to find its features, so
// the ratio here is not the speedup. It is the mechanism's own efficiency, and
// if THAT does not pay there is no point timing anything.
public class ThreatIncrementalCostTests
{
    private static readonly string[] Positions =
    [
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6",
        "r2q1rk1/pp2bppp/2n1bn2/2pp4/3P1B2/2PBPN2/PP1N1PPP/R2Q1RK1 w - - 0 10",
        "3r1rk1/1pq2ppp/p1nbpn2/8/2BP4/2N1PN2/PPQ2PPP/3R1RK1 w - - 0 15",
    ];

    [Fact]
    public void IncrementalTouchesFarFewerRowsThanARefresh()
    {
        Span<int> before = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> after = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> changed = stackalloc int[ThreatDelta.MaxChangedSquares];

        long incrementalRows = 0, refreshRows = 0;
        int moves = 0, kingMoves = 0;

        foreach (string fen in Positions)
        {
            var board = new Board(fen);

            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
                {
                    // King moves refresh in full by design, so they are counted
                    // as refreshes rather than quietly left out of the average.
                    bool kingMove = board.PieceTypeAt(move.From) == PieceType.King
                                 && board.ColorAt(move.From) == perspective;

                    int changedCount = ThreatDelta.ChangedSquares(board, move, changed);
                    var squares = changed[..changedCount].ToArray();

                    ulong affectedBefore = ThreatDelta.AffectedAttackers(board, squares);
                    int beforeCount = ThreatDelta.CollectFrom(board, perspective, affectedBefore, before);
                    var beforeSet = before[..beforeCount].ToArray().ToHashSet();

                    board.MakeMove(move);
                    int refreshCount = ThreatFeatureIndex.ActiveFeatures(board, perspective, after);
                    ulong affectedAfter = ThreatDelta.AffectedAttackers(board, squares);
                    int afterCount = ThreatDelta.CollectFrom(board, perspective, affectedAfter, after);
                    var afterSet = after[..afterCount].ToArray().ToHashSet();
                    board.UnmakeMove();

                    refreshRows += refreshCount;
                    moves++;

                    if (kingMove)
                    {
                        kingMoves++;
                        incrementalRows += refreshCount; // refreshes in full
                        continue;
                    }

                    incrementalRows += beforeSet.Except(afterSet).Count()
                                     + afterSet.Except(beforeSet).Count();
                }
            }
        }

        double incremental = (double)incrementalRows / moves;
        double refresh = (double)refreshRows / moves;

        Assert.True(moves > 200, $"solo {moves} jugadas, muestra insuficiente");

        // MEASURED 2026-08-15 over 268 legal moves from three middlegames:
        // 7.7 rows per move incrementally against 39.3 for a full refresh, so
        // 5.1x less work, and that already includes the 6 king moves counted at
        // full refresh cost. The design note predicted about 6 against about 37.
        //
        // The bound is loose on purpose. What has to hold is that the delta is a
        // FRACTION of the refresh - if it ever stops being one, the incremental
        // path is costing more than the thing it replaced and the design premise
        // is gone. Pinning the exact ratio would just make this fail whenever
        // the position list changes, which is noise rather than a regression.
        Assert.True(incremental < refresh / 2.0,
            $"MEDIDA sobre {moves} jugadas ({kingMoves} de rey, que refrescan entero): "
          + $"incremental {incremental:F1} filas por jugada contra {refresh:F1} de un "
          + $"refresco, {refresh / incremental:F1}x menos. El incremental tiene que "
          + $"quedarse MUY por debajo del refresco o no compensa escribirlo.");
    }
}
