using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// Does the network still count material once a position is lost? For each
// position it prints the static evaluation, then the evaluation with one of
// the losing side's pieces removed. In a healthy evaluator the drop is close
// to the piece's worth; a saturated one barely moves, and that is the
// mechanism behind "the engine gives pieces away when it is losing".
public class SaturationProbe(ITestOutputHelper output)
{
    private static string? EmbeddedModelPath()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            string candidate = Path.Combine(dir, "src", "NoaChess.UCI", "Resources", "noa-embedded.noannue");
            if (File.Exists(candidate))
                return candidate;
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null)
                break;
            dir = parent;
        }
        return null;
    }

    // (label, fen with the losing side to move, square of a piece of that side to remove)
    private static readonly (string, string, string)[] Cases =
    [
        ("control: equal middlegame, remove Nf3",
         "r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8", "f3"),
        ("control: equal middlegame, remove Bd3",
         "r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8", "d3"),
        ("lost -7 (bot game, knight thrown): remove Nb4",
         "7r/P7/8/5k1P/1N2p3/P4pR1/3r1P1K/8 w - - 7 51", "b4"),
        ("lost -7 (bot game): remove Rg3",
         "7r/P7/8/5k1P/1N2p3/P4pR1/3r1P1K/8 w - - 7 51", "g3"),
        ("lost -10 (rook thrown game): remove Rh3",
         "8/6r1/1p4P1/6K1/P7/7R/1k4P1/2q5 w - - 0 60", "h3"),
        ("lost -3 (knight game): remove Nd2",
         "8/8/2k1b3/2n1p3/1p2P1pK/q1P1P3/3N1NP1/2Q5 w - - 1 69", "d2"),
        ("lost -3 (knight game): remove Nf2",
         "8/8/2k1b3/2n1p3/1p2P1pK/q1P1P3/3N1NP1/2Q5 w - - 1 69", "f2"),
        ("lost -7 (suite): remove Nc6",
         "8/8/2N1k3/1n3p1p/1P1p1P1P/3K4/5n2/8 w - - 2 81", "c6"),
    ];

    [Fact]
    public void MaterialStillCountsWhenLosing()
    {
        string? model = EmbeddedModelPath();
        if (model is null)
        {
            output.WriteLine("no embedded model found; probe skipped");
            return;
        }
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);
        var evaluator = new NnueEvaluator(net!);

        output.WriteLine($"{"case",-52} {"eval",7} {"minus piece",12} {"drop",6}");
        foreach (var (label, fen, square) in Cases)
        {
            var board = new Board(fen);
            evaluator.Reset(board);
            int before = evaluator.Evaluate(board);

            string stripped = RemovePiece(fen, square);
            var board2 = new Board(stripped);
            evaluator.Reset(board2);
            int after = evaluator.Evaluate(board2);

            output.WriteLine($"{label,-52} {before,7} {after,12} {before - after,6}");
        }
    }

    // Blank one square in the piece placement field of a FEN.
    private static string RemovePiece(string fen, string square)
    {
        string[] parts = fen.Split(' ');
        string[] ranks = parts[0].Split('/');
        int file = square[0] - 'a';
        int rank = 8 - (square[1] - '0');

        var cells = new List<char>();
        foreach (char ch in ranks[rank])
        {
            if (char.IsDigit(ch))
                for (int i = 0; i < ch - '0'; i++) cells.Add('1');
            else
                cells.Add(ch);
        }
        if (cells[file] == '1')
            throw new InvalidOperationException($"{square} is already empty in {fen}");
        cells[file] = '1';

        var sb = new System.Text.StringBuilder();
        int run = 0;
        foreach (char c in cells)
        {
            if (c == '1') { run++; continue; }
            if (run > 0) { sb.Append(run); run = 0; }
            sb.Append(c);
        }
        if (run > 0) sb.Append(run);
        ranks[rank] = sb.ToString();
        parts[0] = string.Join('/', ranks);
        return string.Join(' ', parts);
    }
}
