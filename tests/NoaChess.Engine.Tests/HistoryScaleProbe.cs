using System.Reflection;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using NoaChess.Engine.Heuristics;
using NoaChess.Engine.Search;
using Xunit;

namespace NoaChess.Engine.Tests;

// TEMPORARY measurement harness (deleted after use): dumps the magnitude
// distribution of the history and continuation-history tables after real
// searches, to calibrate the statScore thresholds ported from the reference
// (its tables are gravity-capped at 14365/29952; ours accumulate depth^2 with
// a 2^20 clamp — the unit ratio must be measured, not guessed).
public class HistoryScaleProbe
{
    [Fact]
    public void MeasureHistoryMagnitudes()
    {
        var search = new AlphaBetaSearch(new ClassicalEvaluator());

        // One deep search from the start position plus two middlegame positions,
        // sharing the same instance like a real game (history persists + decays).
        string[] fens =
        {
            Board.StartFen,
            "r1bq1rk1/pp2ppbp/2np1np1/8/2PNP3/2N1B3/PP2BPPP/R2Q1RK1 w - - 0 9",
            "r2q1rk1/1p1nbppp/p2pbn2/4p3/4P3/1NN1BP2/PPPQ2PP/2KR1B1R w - - 0 11",
        };
        foreach (string fen in fens)
            search.FindBestMove(new Board(fen), SearchLimits.Depth(13));

        var histField = typeof(AlphaBetaSearch).GetField("_history",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var contField = typeof(AlphaBetaSearch).GetField("_contHist",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var hist = (HistoryTable)histField.GetValue(search)!;
        var cont = (ContinuationHistory)contField.GetValue(search)!;

        var histScores = (int[,,])typeof(HistoryTable)
            .GetField("_scores", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(hist)!;
        var contScores = (int[])typeof(ContinuationHistory)
            .GetField("_scores", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(cont)!;

        var histVals = new List<int>();
        foreach (int v in histScores)
            if (v != 0) histVals.Add(Math.Abs(v));

        var contVals = new List<int>();
        foreach (int v in contScores)
            if (v != 0) contVals.Add(Math.Abs(v));

        static string Stats(List<int> vals)
        {
            if (vals.Count == 0) return "EMPTY";
            vals.Sort();
            int P(double p) => vals[(int)(p * (vals.Count - 1))];
            return $"n={vals.Count} p50={P(0.50)} p90={P(0.90)} p99={P(0.99)} max={vals[^1]}";
        }

        // SIGNED distribution. The reference's LMR subtracts this statistic
        // raw, which is only safe if it is centred on zero; its tables are
        // gravity-bounded with symmetric bonus/malus, ours are not (butterfly
        // rescales on the positive rail but clamps on the negative one). A
        // non-zero mean turns "reduce good moves less" into "reduce
        // everything less", i.e. tree inflation.
        static string Signed(List<int> vals)
        {
            if (vals.Count == 0) return "EMPTY";
            vals.Sort();
            long sum = 0;
            int pos = 0;
            foreach (int v in vals)
            {
                sum += v;
                if (v > 0) pos++;
            }
            int P(double p) => vals[(int)(p * (vals.Count - 1))];
            return $"n={vals.Count} mean={sum / (double)vals.Count:F1} "
                 + $"median={P(0.50)} p10={P(0.10)} p90={P(0.90)} positive={100.0 * pos / vals.Count:F1}%";
        }

        var histSigned = new List<int>();
        foreach (int v in histScores)
            if (v != 0) histSigned.Add(v);

        var contSigned = new List<int>();
        foreach (int v in contScores)
            if (v != 0) contSigned.Add(v);

        string outDir = Path.Combine(Path.GetTempPath(), "noachess-probe");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(
            Path.Combine(outDir, "history_scale.txt"),
            $"butterfly ABS: {Stats(histVals)}\n"
          + $"contHist  ABS: {Stats(contVals)}\n\n"
          + $"butterfly SGN: {Signed(histSigned)}\n"
          + $"contHist  SGN: {Signed(contSigned)}\n");
    }
}
