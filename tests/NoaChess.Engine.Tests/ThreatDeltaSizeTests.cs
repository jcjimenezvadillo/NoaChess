using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// How much of a threat refresh a move actually changes.
//
// THE DECISION THIS FEEDS. The incremental threat update is the hardest piece of
// work this engine would take on: a move changes not only the moved piece's own
// threats but every DISCOVERED one, where a slider's ray opens or closes through
// the square it vacated or occupied. Before writing it, it is worth knowing what
// it can possibly buy - and that is bounded by how much of the feature set
// survives a move unchanged.
//
// If a typical move leaves most features standing, an incremental update replaces
// a full rebuild with a handful of row additions and is worth the difficulty. If
// it churns most of them, the difficulty buys very little and the honest answer
// is to leave the refresh alone and spend the time elsewhere.
//
// Measured as a set difference between the full refresh before and after, which
// is the ground truth ANY incremental implementation has to reproduce exactly -
// so this test also defines what correct means for the work it is sizing.
public class ThreatDeltaSizeTests
{
    private static readonly string[] Positions =
    [
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2NP1N2/PPP2PPP/R1BQK2R w KQkq - 0 6",
        "r2q1rk1/pp2bppp/2n1bn2/2pp4/3P1B2/2PBPN2/PP1N1PPP/R2Q1RK1 w - - 0 10",
        "r1bq1rk1/1p1nbppp/p2ppn2/6B1/2BNP3/2N5/PPP1QPPP/2KR3R w - - 0 11",
        "8/2k5/8/8/3PP3/8/5K2/8 w - - 0 1",
    ];

    [Fact]
    public void MostThreatFeaturesSurviveAMove()
    {
        Span<int> before = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];
        Span<int> after = stackalloc int[ThreatFeatureIndex.MaxActiveFeatures];

        int moves = 0;
        long totalActive = 0, totalChanged = 0;
        int worstChanged = 0;
        double worstShare = 0;

        foreach (string fen in Positions)
        {
            var board = new Board(fen);
            Color side = board.SideToMove;

            foreach (Move move in MoveGenerator.GenerateLegalMoves(board).ToArray())
            {

                int beforeCount = ThreatFeatureIndex.ActiveFeatures(board, side, before);
                var beforeSet = before[..beforeCount].ToArray().ToHashSet();

                board.MakeMove(move);
                int afterCount = ThreatFeatureIndex.ActiveFeatures(board, side, after);
                var afterSet = after[..afterCount].ToArray().ToHashSet();
                board.UnmakeMove();

                int changed = beforeSet.Except(afterSet).Count() + afterSet.Except(beforeSet).Count();
                int active = Math.Max(beforeCount, afterCount);

                moves++;
                totalActive += active;
                totalChanged += changed;
                if (changed > worstChanged) worstChanged = changed;
                if (active > 0 && (double)changed / active > worstShare)
                    worstShare = (double)changed / active;
            }
        }

        Assert.True(moves > 100, $"only {moves} legal moves examined");

        double meanActive = (double)totalActive / moves;
        double meanChanged = (double)totalChanged / moves;
        double share = meanChanged / meanActive;

        // MEASURED 2026-08-15 over 168 legal moves: 36.8 active features on
        // average, 6.2 of them changing - so 83% survive a move untouched. An
        // incremental update would replace about 37 row additions with about 6,
        // a sixfold reduction in accumulator work, which is what justifies
        // writing the hardest piece of code in this engine.
        //
        // The worst case is 141%, MORE changes than there were active features,
        // and it is not an anomaly: it is a KING move. The king decides the
        // mirror, so moving it reorients the whole board and every feature is
        // renumbered. HalfKA already handles that by refreshing in full on a
        // king move, and the threat accumulator will do the same - incremental
        // for ordinary moves, full refresh when the perspective's king moves.
        //
        // The bound below is loose on purpose: this reports a number, and a test
        // that fails because a different set of positions shifted the mean would
        // be noise. What it pins is that a move does not churn everything, which
        // is the premise the incremental work rests on.
        Assert.True(share is > 0 and < 0.5,
            $"MEDIDA sobre {moves} jugadas legales: {meanActive:F1} features activas de media, "
          + $"{meanChanged:F1} cambian ({100 * share:F1}%). Peor caso {worstChanged} cambios, "
          + $"peor proporcion {100 * worstShare:F0}%.");
    }
}
