using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// The PERSPECTIVE-FREE delta - relations collected once as packed pairs,
// differenced once, and numbered afterwards - against the same oracle the
// indexed one is held to: a full refresh before the move against a full refresh
// after it.
//
// WHY IT IS TESTED AGAINST THE REFRESH AND NOT AGAINST CollectFrom. Checking
// the new path against the old one would only prove they agree, which is worth
// having and is the second test below. It would not catch a fault they share.
// The refresh is the definition of what the accumulator should hold, so that is
// the bar.
//
// WHAT COULD GO WRONG HERE AND NOWHERE ELSE. Differencing in pair space and
// numbering afterwards is only equivalent to numbering first if the map from
// pair to index is injective - two distinct relations sharing one index would
// let a survivor cancel a change. The argument that it is injective is written
// at ThreatFeatureIndex.Pack; this is the measurement of it.
public class ThreatPairDeltaTests
{
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
        // Blocked pawns of both colours, which is the one threat relation that
        // is not an attack and so is not reachable through attackersTo.
        "8/pp3k2/2p5/1pP5/1P6/5K2/P7/8 w - - 0 1",
    ];

    private static HashSet<int> Numbered(ReadOnlySpan<int> pairs, Color perspective, int kingSquare)
    {
        var set = new HashSet<int>();
        foreach (int packed in pairs)
        {
            int index = ThreatFeatureIndex.IndexOfPacked(perspective, kingSquare, packed);
            if (index >= 0)
                set.Add(index);
        }
        return set;
    }

    [Fact]
    public void PairDeltaMatchesFullRefreshOnEveryLegalMove()
    {
        Span<int> full = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> pairs = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> changed = stackalloc int[ThreatDelta.MaxChangedSquares];

        int moves = 0;
        var failures = new List<string>();

        foreach (string fen in Positions)
        {
            var board = new Board(fen);

            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
                {
                    // A king move of this perspective renumbers every feature,
                    // so the accumulator rebuilds instead of differencing.
                    if (board.PieceTypeAt(move.From) == PieceType.King
                        && board.ColorAt(move.From) == perspective)
                        continue;

                    int kingSquare = board.KingSquare(perspective);

                    int fullBefore = ThreatFeatureIndex.ActiveFeatures(board, perspective, full);
                    var truthBefore = full[..fullBefore].ToArray().ToHashSet();

                    int changedCount = ThreatDelta.ChangedSquares(board, move, changed);
                    var changedSquares = changed[..changedCount].ToArray();

                    // Each side over its OWN affected set, which is the only
                    // shape the engine can run: the pre-move board is gone by
                    // the time the after side is collected.
                    ulong affectedBefore = ThreatDelta.AffectedAttackers(board, changedSquares);
                    int beforeCount = ThreatDelta.CollectPairs(board, affectedBefore, pairs);
                    var pairsBefore = pairs[..beforeCount].ToArray();

                    board.MakeMove(move);

                    int fullAfter = ThreatFeatureIndex.ActiveFeatures(board, perspective, full);
                    var truthAfter = full[..fullAfter].ToArray().ToHashSet();

                    ulong affectedAfter = ThreatDelta.AffectedAttackers(board, changedSquares);
                    int afterCount = ThreatDelta.CollectPairs(board, affectedAfter, pairs);
                    var pairsAfter = pairs[..afterCount].ToArray();

                    board.UnmakeMove();
                    moves++;

                    // Differenced as PAIRS, numbered afterwards - the order the
                    // engine now uses.
                    var removed = pairsBefore.Except(pairsAfter).ToArray();
                    var added = pairsAfter.Except(pairsBefore).ToArray();

                    var mineRemoved = Numbered(removed, perspective, kingSquare);
                    var mineAdded = Numbered(added, perspective, kingSquare);

                    var truthRemoved = truthBefore.Except(truthAfter).ToHashSet();
                    var truthAdded = truthAfter.Except(truthBefore).ToHashSet();

                    if ((!truthRemoved.SetEquals(mineRemoved) || !truthAdded.SetEquals(mineAdded))
                        && failures.Count < 6)
                    {
                        failures.Add(
                            fen + " | " + move + " | " + perspective + "\n" +
                            "  removed missing: " + string.Join(",", truthRemoved.Except(mineRemoved)) + "\n" +
                            "  removed extra:   " + string.Join(",", mineRemoved.Except(truthRemoved)) + "\n" +
                            "  added missing:   " + string.Join(",", truthAdded.Except(mineAdded)) + "\n" +
                            "  added extra:     " + string.Join(",", mineAdded.Except(truthAdded)));
                    }
                }
            }
        }

        Assert.True(moves > 400, "only " + moves + " moves exercised");
        Assert.True(failures.Count == 0,
            failures.Count + " move(s) disagreed with the full refresh:\n"
            + string.Join("\n", failures));
    }

    // The new collector against the old one, which is the check that the target
    // masks added along the way changed the LENGTH of the lists and not their
    // meaning. CollectFrom filtered with '& occupied' and let the index reject
    // what the schema does not record; CollectPairs rejects it up front.
    [Fact]
    public void PairCollectorAgreesWithTheIndexedOne()
    {
        Span<int> pairs = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> indexed = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];

        int cases = 0;

        foreach (string fen in Positions)
        {
            var board = new Board(fen);

            foreach (Color perspective in new[] { Color.White, Color.Black })
            {
                int kingSquare = board.KingSquare(perspective);

                // Every single-square affected set the board can produce, which
                // covers far more attacker mixes than the legal moves do.
                for (int square = 0; square < 64; square++)
                {
                    ulong attackers = 1UL << square;
                    attackers |= ThreatDelta.AffectedAttackers(board, new[] { square });

                    int pairCount = ThreatDelta.CollectPairs(board, attackers, pairs);
                    int indexCount = ThreatDelta.CollectFrom(board, perspective, attackers, indexed);

                    var mine = Numbered(pairs[..pairCount], perspective, kingSquare);
                    var theirs = indexed[..indexCount].ToArray().ToHashSet();

                    Assert.True(mine.SetEquals(theirs),
                        fen + " | " + perspective + " | square " + square + "\n" +
                        "  missing: " + string.Join(",", theirs.Except(mine)) + "\n" +
                        "  extra:   " + string.Join(",", mine.Except(theirs)));
                    cases++;
                }
            }
        }

        Assert.True(cases > 1000, "only " + cases + " affected sets exercised");
    }
}
