using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// What the perspective-free delta actually costs, counted rather than timed.
//
// WHY COUNTED. The gain comes from doing the geometry and the set difference
// ONCE instead of once per perspective, which sounds like a clean halving and
// is not: the pair lists are LONGER than the index lists, because a symmetric
// relation - a knight attacking a knight - is generated from both ends and only
// one end survives numbering. The difference is quadratic in the list length,
// so a list 1.4x longer would cancel the halving outright.
//
// That is a ratio between two counts, so it can be settled exactly and without
// a clock, which matters here: a stopwatch on this machine has been wrong about
// this file before.
public class ThreatPairCostTests
{
    private static readonly string[] Positions =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6",
        "r2q1rk1/pp2bppp/2n1bn2/2pp4/3P1B2/2PBPN2/PP1N1PPP/R2Q1RK1 w - - 0 10",
        "3r1rk1/1pq2ppp/p1nbpn2/8/2BP4/2N1PN2/PPQ2PPP/3R1RK1 w - - 0 15",
        "r1bq1rk1/1p1nbppp/p2ppn2/6B1/2BNP3/2N5/PPP1QPPP/2KR3R w - - 0 11",
    ];

    // The scan the delta runs, counting comparisons WITH the early exit, since
    // most lookups hit and stop long before the end.
    private static long Comparisons(ReadOnlySpan<int> haystack, int needle)
    {
        for (int i = 0; i < haystack.Length; i++)
            if (haystack[i] == needle)
                return i + 1;
        return haystack.Length;
    }

    private static long DiffCost(ReadOnlySpan<int> before, ReadOnlySpan<int> after)
    {
        long total = 0;
        for (int i = 0; i < before.Length; i++)
            total += Comparisons(after, before[i]);
        for (int i = 0; i < after.Length; i++)
            total += Comparisons(before, after[i]);
        return total;
    }

    [Fact]
    public void PairSpaceCostsLessThanNumberingTwice()
    {
        Span<int> pairsBefore = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> pairsAfter = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> idxBefore = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> idxAfter = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> changed = stackalloc int[ThreatDelta.MaxChangedSquares];

        long pairDiff = 0, indexDiff = 0;
        long pairLen = 0, indexLen = 0;
        long pairCollects = 0, indexCollects = 0;
        int moves = 0;

        foreach (string fen in Positions)
        {
            var board = new Board(fen);

            foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
            {
                if (board.PieceTypeAt(move.From) == PieceType.King)
                    continue; // refreshes in full either way

                int changedCount = ThreatDelta.ChangedSquares(board, move, changed);
                var squares = changed[..changedCount].ToArray();

                ulong affectedBefore = ThreatDelta.AffectedAttackers(board, squares);
                int pb = ThreatDelta.CollectPairs(board, affectedBefore, pairsBefore);
                int ib0 = ThreatDelta.CollectFrom(board, Color.White, affectedBefore, idxBefore);

                board.MakeMove(move);
                ulong affectedAfter = ThreatDelta.AffectedAttackers(board, squares);
                int pa = ThreatDelta.CollectPairs(board, affectedAfter, pairsAfter);
                int ia0 = ThreatDelta.CollectFrom(board, Color.White, affectedAfter, idxAfter);
                board.UnmakeMove();

                // New: geometry twice per node, difference once.
                pairCollects += 2;
                pairLen += pb + pa;
                pairDiff += DiffCost(pairsBefore[..pb], pairsAfter[..pa]);

                // Old: geometry four times per node, difference twice. Both
                // perspectives produce lists of the same LENGTH - the same
                // relations, renumbered - so one measured pass doubled is the
                // honest count and avoids pretending the two differ.
                indexCollects += 4;
                indexLen += 2L * (ib0 + ia0);
                indexDiff += 2L * DiffCost(idxBefore[..ib0], idxAfter[..ia0]);

                moves++;
            }
        }

        double diffRatio = (double)indexDiff / pairDiff;
        double lengthRatio = (double)(pairLen / 2.0) / (indexLen / 4.0);

        string report =
            "moves                 " + moves + "\n" +
            "collect calls   old   " + indexCollects + "\n" +
            "collect calls   new   " + pairCollects + "\n" +
            "list length     old   " + (indexLen / (double)(4 * moves)).ToString("F1") + " per collect\n" +
            "list length     new   " + (pairLen / (double)(2 * moves)).ToString("F1") + " per collect\n" +
            "               ratio  " + lengthRatio.ToString("F3") + "\n" +
            "diff compares   old   " + indexDiff + "\n" +
            "diff compares   new   " + pairDiff + "\n" +
            "               saved  " + ((1 - 1 / diffRatio) * 100).ToString("F1") + "%\n";

        File.WriteAllText("threat_pair_cost.txt", report);

        // The point of the change. If the pair lists were long enough to cancel
        // the halving this would land at or below 1.0 and the redesign would be
        // worthless, which is exactly what is being checked.
        Assert.True(diffRatio > 1.2,
            "the difference got no cheaper in pair space:\n" + report);
        Assert.Equal(2 * moves, (int)pairCollects);
    }
}
