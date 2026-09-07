using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;

namespace NoaChess.Engine.Tests;

// The network must score a position and its colour-flipped mirror alike, and
// ten hand-picked positions are a thin guarantee. This runs the same check
// over the 60-position node bench when it is available next to the repo, so a
// colour asymmetry anywhere in feature indexing or accumulator handling would
// surface on ordinary middlegame and endgame material.
public class EvalSymmetryBenchTests
{
    private static string? Find(string relative)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null) break;
            dir = parent;
        }
        return null;
    }

    private static string Mirror(string fen)
    {
        string[] parts = fen.Split(' ');
        string[] ranks = parts[0].Split('/');
        Array.Reverse(ranks);
        var sb = new System.Text.StringBuilder();
        foreach (char c in string.Join('/', ranks))
            sb.Append(char.IsLetter(c) ? (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
        string side = parts[1] == "w" ? "b" : "w";
        string castling = parts[2] == "-" ? "-" :
            new string(parts[2].Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).OrderBy(c => "KQkq".IndexOf(c)).ToArray());
        string ep = parts[3] == "-" ? "-" : $"{parts[3][0]}{(char)('0' + (9 - (parts[3][1] - '0')))}";
        return $"{sb} {side} {castling} {ep} {parts[4]} {parts[5]}";
    }

    [Fact]
    public void TheNetworkScoresEveryBenchPositionAndItsMirrorAlike()
    {
        string? model = Find(Path.Combine("src", "NoaChess.UCI", "Resources", "noa-embedded.noannue"));
        string? fens = Find(Path.Combine("..", "_______________CHESSTEST", "bench.fens"))
                    ?? Find(Path.Combine("..", "..", "_______________CHESSTEST", "bench.fens"));
        if (model is null || fens is null)
            return; // Nothing to check against on this machine.
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);
        var evaluator = new NnueEvaluator(net!);

        int checkedCount = 0;
        foreach (string raw in File.ReadAllLines(fens))
        {
            string fen = raw.Trim();
            if (fen.Length == 0 || fen.StartsWith('#')) continue;
            var a = new Board(fen);
            var b = new Board(Mirror(fen));
            evaluator.Reset(a);
            int ea = evaluator.Evaluate(a);
            evaluator.Reset(b);
            int eb = evaluator.Evaluate(b);
            Assert.True(ea == eb, $"{fen}: {ea} against its mirror's {eb}");
            checkedCount++;
        }
        Assert.True(checkedCount >= 10);
    }
}
