using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Search;
using NoaChess.Engine.Tablebases;

namespace NoaChess.Engine.Tests;

// Root tablebase filtering, audit of 2026-10-04.
public class TablebaseRootTests
{
    private static void EnsureInit()
    {
        string tb = SyzygyTestEnvironment.TablebasePath!;
        if (Syzygy.CurrentPath != tb)
            Syzygy.Init(tb);
    }

    private static AlphaBetaSearch NewSearch()
    {
        EnsureInit();
        var search = new AlphaBetaSearch(new ClassicalEvaluator());
        search.SyzygyProbeLimit = 7; // computes the loaded piece limit
        return search;
    }

    private static bool RootLostInTb(AlphaBetaSearch search) =>
        (bool)typeof(AlphaBetaSearch)
            .GetField("_rootLostInTb", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(search)!;

    // A lost root whose fifty-move counter is already high enough that the
    // defender can reach it: the tables score the position for a ZEROED
    // counter, so every move that keeps the game reversible until move 100
    // draws, and every capture, pawn move or too-short move hands the winner a
    // fresh counter. Thirty such positions from real endings, each with the
    // moves that keep the draw, all of them (checked against the tables with
    // an independent prober: a move keeps it when the position after it is
    // not lost, or when it is reversible and the winner's DTZ from there
    // passes the counter); the engine played a losing move in 11 to 12 of
    // them before the root ranked lost positions by DTZ and the counter.
    // Format: FEN | moves that keep the draw.
    private static readonly string[] ClockSaves =
    [
        "1Q6/8/8/1p3r2/8/7K/5k2/8 b - - 84 80|f2f3 f2e3",
        "5Q2/8/8/2p1r3/8/8/2k3K1/8 b - - 78 80|e5d5 c2d3",
        "8/r7/3K4/8/8/1Q1p4/8/5k2 b - - 76 80|f1e2",
        "8/1Q6/8/8/6r1/k7/4K2p/8 b - - 66 80|g4h4 g4b4",
        "1r4k1/3Q3p/8/8/8/8/8/3K4 b - - 72 80|b8f8",
        "5Q2/8/6k1/7p/4r3/7K/8/8 b - - 86 80|e4e6 e4e5 e4g4 e4e3",
        "8/8/8/8/5p2/4r2k/3Q4/6K1 b - - 54 80|h3g3 e3g3",
        "8/1r6/2k5/6Q1/8/p7/5K2/8 b - - 74 80|b7f7 b7a7",
        "4Q3/3p3K/8/2kr4/8/8/8/8 b - - 80 80|d5d6 d5d4 d5d3 d5d2 d5d1 c5d6 c5c6 c5b6 c5b5 c5d4 c5c4 c5b4",
        "3r4/8/8/6k1/7p/8/K4Q2/8 b - - 82 80|d8h8 d8a8 g5g4",
        "8/5b2/7K/8/8/8/4pQ2/1k6 b - - 86 80|f7c4",
        "8/2Q5/5k2/3r3p/8/8/4K3/8 b - - 86 80|f6g5 f6f5 d5g5 d5f5 d5e5",
        "2r5/8/3kp3/8/6K1/4Q3/8/8 b - - 84 80|c8g8 c8e8 c8c7 c8c6 c8c5 c8c4 d6e7 d6d7 d6d5",
        "5Q2/8/8/4r1k1/2p5/8/8/K7 b - - 86 80|e5e4 e5e1",
        "5Q2/r7/8/8/2p5/8/6K1/4k3 b - - 80 80|a7a2",
        "4k3/7r/3p4/8/8/6Q1/8/2K5 b - - 74 80|h7d7 h7c7",
        "3r4/6K1/8/Q7/8/2p5/8/5k2 b - - 82 80|d8d7 d8d3",
        "K7/8/8/4pk2/8/4Q3/6r1/8 b - - 84 80|g2g8 g2g7 g2g6 g2g5 g2g4 g2h2 g2b2 g2a2",
        "1Q4K1/8/6kr/8/5p2/8/8/8 b - - 74 80|g6f5",
        "6K1/8/8/4p3/Q7/8/3r4/2k5 b - - 84 80|d2g2 d2e2",
        "8/4r3/8/2p5/4k3/Q7/8/4K3 b - - 84 80|e7e5 e4f5 e4d5 e4d4",
        "8/6pQ/3k4/5r2/8/8/4K3/8 b - - 76 80|f5f7 f5g5 f5e5",
        "2Q5/7p/5k2/8/5r2/2K5/8/8 b - - 78 80|f6g7 f6g6 f6g5 f4f5 f4f3",
        "4K3/pk6/8/2r5/3Q4/8/8/8 b - - 82 80|c5c8",
        "2Q5/8/rk6/8/8/1p6/6K1/8 b - - 76 80|a6a2",
        "8/8/8/5Q2/8/k7/1p5K/8 b - - 86 80|a3a2",
        "8/1Q6/8/8/p2k4/4r3/7K/8 b - - 82 80|e3e4 e3e2",
        "8/6K1/8/1p6/4k3/8/r7/2Q5 b - - 84 80|a2a7",
        "8/4k3/4pr2/8/8/4Q3/6K1/8 b - - 66 80|e7f7 e7d7 f6f7 f6g6 f6f5",
        "8/8/8/k1p1r3/8/6Q1/6K1/8 b - - 76 80|e5e7 e5d5",
    ];

    [SyzygyFact]
    public void ALossTheClockHasSaved_PlaysASavingMove()
    {
        var failures = new List<string>();
        foreach (string line in ClockSaves)
        {
            string[] parts = line.Split('|');
            var board = new Board(parts[0]);
            HashSet<string> keep = [.. parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)];

            AlphaBetaSearch search = NewSearch();
            SearchResult result = search.FindBestMove(board, SearchLimits.Depth(6));
            if (!keep.Contains(result.BestMove.ToString()))
                failures.Add($"{parts[0]}: played {result.BestMove}, saves {parts[1]}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    // A root move that repeats a position seen only once before is no draw:
    // the winner simply does not repeat. The ranking used to score it as one
    // and kept it among the clock's saves (review of 2026-10-04: 2 of 60
    // four-ply histories looping back to the positions above played a loss
    // that way). Each root here is reached through such a loop, and only the
    // save with the widest margin over the counter is kept. Control, third
    // row: with the loop played twice the same move completes a threefold, a
    // real draw, and it is the move played.
    [SyzygyTheory]
    [InlineData("1Q6/8/8/1p3r2/8/7K/5k2/8 b - - 80 78", "f5f3 h3h4 f3f5 h4h3", "f2e3")]
    [InlineData("5Q2/8/8/2p1r3/8/8/2k3K1/8 b - - 74 78", "e5g5 g2h3 g5e5 h3g2", "e5d5")]
    [InlineData("1Q6/8/8/1p6/8/5r1K/5k2/8 w - - 77 77", "h3h4 f3f5 h4h3 f5f3 h3h4 f3f5 h4h3", "f5f3")]
    public void ATwofoldIsNotASave_AThreefoldIsADraw(string fen, string history, string expected)
    {
        var board = new Board(fen);
        foreach (string uci in history.Split(' '))
            board.MakeMove(MoveGenerator.GenerateLegalMoves(board).First(m => m.ToString() == uci));

        AlphaBetaSearch search = NewSearch();
        SearchResult result = search.FindBestMove(board, SearchLimits.Depth(6));
        Assert.Equal(expected, result.BestMove.ToString());
    }

    // The pawnless lost root with nothing to capture keeps its longest-DTZ
    // band (TbResistance's root case), but only when no move can reach the
    // fifty-move draw: with a save available the root is ranked as above and
    // never marked lost. Both positions were found with an independent
    // prober, and in each exactly one move saves; in the first, the band of
    // four plies would also keep two moves that do not (Re8, Ra8).
    [SyzygyTheory]
    [InlineData("3r4/8/5Q2/K7/8/8/8/3k4 b - - 60 80", "d8d3")]
    [InlineData("4Q3/r7/7K/8/7k/8/8/8 b - - 87 80", "a7a6")]
    public void APawnlessLossWithASave_IsNotTreatedAsLost(string fen, string save)
    {
        AlphaBetaSearch search = NewSearch();
        SearchResult result = search.FindBestMove(new Board(fen), SearchLimits.Depth(6));
        Assert.Equal(save, result.BestMove.ToString());
        Assert.False(RootLostInTb(search));

        // Control: the same position at a zero counter cannot be saved, and
        // takes the lost-root path with its band.
        string zeroed = fen.Replace(" 60 80", " 0 80").Replace(" 87 80", " 0 80");
        AlphaBetaSearch control = NewSearch();
        control.FindBestMove(new Board(zeroed), SearchLimits.Depth(6));
        Assert.True(RootLostInTb(control));
    }

    // A Lazy SMP root ranking stamped with the wrong search's serial (a helper
    // in quarantine still inside the root filter of an older search) handed
    // the next search the root moves of another position. The intersection
    // with this root's moves was empty, and an empty root list spins the
    // aspiration loop forever: no bestmove, ever. The root must be ranked
    // afresh instead.
    [SyzygyFact]
    public void ARootRankingFromAnotherPosition_DoesNotEmptyTheRoot()
    {
        AlphaBetaSearch search = NewSearch();
        Type memoType = typeof(AlphaBetaSearch).Assembly.GetType("NoaChess.Engine.Search.RootTablebaseMemo")!;
        object memo = Activator.CreateInstance(memoType)!;
        memoType.GetField("Serial")!.SetValue(memo, 7);
        // e2e4: no piece on e2 below, so no move of this root survives it.
        memoType.GetField("RootMoves")!.SetValue(memo, new[] { new Move(12, 28, MoveFlag.Quiet) });
        typeof(AlphaBetaSearch).GetField("RootTbMemo", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(search, memo);
        typeof(AlphaBetaSearch).GetField("SearchSerial", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(search, 7);

        const string fen = "k7/8/1QK5/8/8/8/8/8 w - - 0 1";
        var task = Task.Run(() => search.FindBestMove(new Board(fen), SearchLimits.Time(200)));
        Assert.True(task.Wait(5000), "the search never returned");
        Assert.Contains(task.Result.BestMove, MoveGenerator.GenerateLegalMoves(new Board(fen)));
    }
}
