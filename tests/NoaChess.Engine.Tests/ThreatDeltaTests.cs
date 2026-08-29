using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// The incremental threat delta against the only oracle that counts: the set
// difference between a FULL refresh before the move and a FULL refresh after.
//
// This is the test the whole incremental design rests on. A delta that misses
// one feature does not crash and does not fail a game - it leaves the
// accumulator holding a row it should have removed, and every evaluation from
// that node onwards is wrong by an amount nobody can see. So the bar here is
// exact set equality on every legal move, not a tolerance.
public class ThreatDeltaTests
{
    // Chosen for the cases where "the piece moved from A to B" is an
    // incomplete description of what happened: castling moves two pieces, en
    // passant removes a pawn from neither square, promotion changes a piece's
    // TYPE, and a discovered line opens a slider that never moved.
    private static readonly string[] Positions =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6",
        "r2q1rk1/pp2bppp/2n1bn2/2pp4/3P1B2/2PBPN2/PP1N1PPP/R2Q1RK1 w - - 0 10",
        "r1bq1rk1/1p1nbppp/p2ppn2/6B1/2BNP3/2N5/PPP1QPPP/2KR3R w - - 0 11",
        // Castling available to both sides, kingside and queenside.
        "r3k2r/pppq1ppp/2n1bn2/3pp3/3PP3/2N1BN2/PPPQ1PPP/R3K2R w KQkq - 0 9",
        // En passant actually available.
        "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3",
        // Promotions, with and without capture.
        "8/1P4k1/8/8/8/8/6K1/1r6 w - - 0 1",
        "1n6/1P4k1/8/8/8/8/6K1/8 w - - 0 1",
        // Heavy sliders: discovered lines everywhere.
        "3r1rk1/1pq2ppp/p1nbpn2/8/2BP4/2N1PN2/PPQ2PPP/3R1RK1 w - - 0 15",
        "8/2k5/8/8/3PP3/8/5K2/8 w - - 0 1",
    ];

    [Fact]
    public void DeltaMatchesFullRefreshOnEveryLegalMove()
    {
        Span<int> full = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> partial = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> changed = stackalloc int[ThreatDelta.MaxChangedSquares];

        int moves = 0, kingMoves = 0;
        var failures = new List<string>();

        foreach (string fen in Positions)
        {
            var board = new Board(fen);

            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
                {
                    // A king move reorients the whole board and renumbers every
                    // feature, which is why the accumulator refreshes in full
                    // there instead. Excluded here on the same grounds.
                    if (board.PieceTypeAt(move.From) == PieceType.King
                        && board.ColorAt(move.From) == perspective)
                    {
                        kingMoves++;
                        continue;
                    }

                    int fullBefore = ThreatFeatureIndex.ActiveFeatures(board, perspective, full);
                    var truthBefore = full[..fullBefore].ToArray().ToHashSet();

                    // ASYMMETRIC, and this is the shape the engine can actually
                    // run rather than the tidiest one.
                    //
                    // The accumulator computes its "before" side while the board
                    // is still in the pre-move position and its "after" side
                    // once the move is made; the pre-move board is gone by then,
                    // so it can never collect "before" over a set unioned across
                    // the move. Each side therefore uses its OWN affected set.
                    //
                    // That is sound by the same argument: an attacker present
                    // only in the after-set stands on a square that was empty
                    // before, so it generated nothing before; an attacker only
                    // in the before-set is gone after, so it generates nothing
                    // after; and one in both is differenced normally. The test
                    // exists to prove that rather than to trust it.
                    int changedCount = ThreatDelta.ChangedSquares(board, move, changed);
                    var changedSquares = changed[..changedCount].ToArray();

                    ulong affectedBefore = ThreatDelta.AffectedAttackers(board, changedSquares);
                    int partialBefore = ThreatDelta.CollectFrom(board, perspective, affectedBefore, partial);
                    var mineBefore = partial[..partialBefore].ToArray().ToHashSet();

                    board.MakeMove(move);

                    int fullAfter = ThreatFeatureIndex.ActiveFeatures(board, perspective, full);
                    var truthAfter = full[..fullAfter].ToArray().ToHashSet();

                    ulong affectedAfter = ThreatDelta.AffectedAttackers(board, changedSquares);
                    int partialAfter = ThreatDelta.CollectFrom(board, perspective, affectedAfter, partial);
                    var mineAfter = partial[..partialAfter].ToArray().ToHashSet();

                    var truthRemoved = truthBefore.Except(truthAfter).ToHashSet();
                    var truthAdded = truthAfter.Except(truthBefore).ToHashSet();
                    var mineRemoved = mineBefore.Except(mineAfter).ToHashSet();
                    var mineAdded = mineAfter.Except(mineBefore).ToHashSet();

                    board.UnmakeMove();
                    moves++;

                    if (!truthRemoved.SetEquals(mineRemoved) || !truthAdded.SetEquals(mineAdded))
                    {
                        if (failures.Count < 6)
                        {
                            failures.Add(
                                $"{fen} | {perspective} | {move.From}->{move.To} {move.Flag}\n"
                              + $"    quitadas: verdad {truthRemoved.Count} mias {mineRemoved.Count}"
                              + $"  faltan {string.Join(",", truthRemoved.Except(mineRemoved))}"
                              + $"  sobran {string.Join(",", mineRemoved.Except(truthRemoved))}\n"
                              + $"    puestas: verdad {truthAdded.Count} mias {mineAdded.Count}"
                              + $"  faltan {string.Join(",", truthAdded.Except(mineAdded))}"
                              + $"  sobran {string.Join(",", mineAdded.Except(truthAdded))}");
                        }
                    }
                }
            }
        }

        Assert.True(moves > 400, $"solo {moves} jugadas examinadas, muestra insuficiente");
        Assert.True(failures.Count == 0,
            $"{failures.Count} jugadas con delta incorrecto de {moves} "
          + $"({kingMoves} de rey saltadas, esas refrescan entero):\n"
          + string.Join("\n", failures));
    }
}
