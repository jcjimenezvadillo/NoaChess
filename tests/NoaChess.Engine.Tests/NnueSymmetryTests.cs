using System.Text;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Nnue;
using Xunit;
using Xunit.Abstractions;

namespace NoaChess.Engine.Tests;

// Colour symmetry of the network that actually plays. HalfKA features are
// written from each side's own perspective, so the vertical mirror of a
// position (ranks flipped, colours swapped, side to move swapped) presents the
// transformer with the same two accumulators in the same roles. The score must
// therefore come back identical, whatever the weights say. A difference is a
// bug in the feature indexing, the king bucket, or the perspective handling -
// the parts of the pipeline no golden-value test can cover end to end.
//
// The classical evaluator has the same test in EvalSymmetryTests; this one
// covers the path the engine really uses.
public class NnueSymmetryTests(ITestOutputHelper output)
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

    // Ranks reversed, piece case swapped, side to move swapped, castling
    // rights and the en passant square mirrored with them.
    private static string MirrorFen(string fen)
    {
        string[] parts = fen.Split(' ');
        string[] ranks = parts[0].Split('/');
        var sb = new StringBuilder();
        for (int i = ranks.Length - 1; i >= 0; i--)
        {
            foreach (char ch in ranks[i])
                sb.Append(char.IsLetter(ch) ? (char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch)) : ch);
            if (i > 0) sb.Append('/');
        }

        string stm = parts[1] == "w" ? "b" : "w";
        string castling = parts[2];
        if (castling != "-")
        {
            var cb = new StringBuilder();
            foreach (char want in "KQkq")
                foreach (char ch in castling)
                    if ((char.IsUpper(ch) ? char.ToLower(ch) : char.ToUpper(ch)) == want)
                        cb.Append(want);
            castling = cb.Length > 0 ? cb.ToString() : "-";
        }

        string ep = parts[3];
        if (ep != "-")
            ep = $"{ep[0]}{9 - (ep[1] - '0')}";

        return $"{sb} {stm} {castling} {ep} {parts[4]} {parts[5]}";
    }

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R w KQ - 0 8")]
    [InlineData("r2qk2r/ppp2ppp/2npbn2/2b1p3/2B1P3/2NPBN2/PPP2PPP/R2QK2R w KQkq - 0 8")]
    // Kings on opposite wings, so the king buckets differ between the two.
    [InlineData("2kr3r/pppq1ppp/2np1n2/4p3/4P3/2NP1N2/PPPQ1PPP/2KR3R w - - 0 10")]
    // A king past the mirror boundary, where the feature index flips files.
    [InlineData("8/8/4k3/8/8/3K4/6P1/8 w - - 0 40")]
    [InlineData("7k/8/8/1N6/2P5/8/5PPP/6K1 w - - 0 40")]
    // Sharp material imbalance: queen against rook and minor.
    [InlineData("4rbk1/5ppp/8/8/8/8/5PPP/3Q2K1 w - - 0 30")]
    // An en passant square that a capturer can actually use.
    [InlineData("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq f6 0 3")]
    // Endgame with a passed pawn on each wing.
    [InlineData("8/1p4k1/8/8/8/8/6PK/8 w - - 0 50")]
    // Black to move, so the mirror is a white-to-move position.
    [InlineData("r1bq1rk1/pp2bppp/2n1pn2/2pp4/3P4/2PBPN2/PP1N1PPP/R2QK2R b KQ - 0 8")]
    public void TheNetworkScoresAPositionAndItsMirrorAlike(string fen)
    {
        string? model = EmbeddedModelPath();
        if (model is null)
        {
            output.WriteLine("no embedded model found; probe skipped");
            return;
        }
        Assert.True(NnueModelLoader.TryLoad(model, out NnueNetwork? net, out string error), error);

        string mirrored = MirrorFen(fen);
        // Reset installs the accumulator for the board; without it Evaluate reads
        // an empty accumulator and returns the same constant for every position,
        // which is how the first version of this test passed while checking
        // nothing (found 2026-09-07).
        var evaluator = new NnueEvaluator(net!);
        var first = new Board(fen);
        evaluator.Reset(first);
        int direct = evaluator.Evaluate(first);
        var second = new Board(mirrored);
        evaluator.Reset(second);
        int reflected = evaluator.Evaluate(second);
        // Guard against the unreset-evaluator trap: two different positions must
        // not evaluate alike. The start position is the second one.
        var start = new Board(Board.StartFen);
        evaluator.Reset(start);
        if (fen != Board.StartFen)
            Assert.NotEqual(evaluator.Evaluate(start), direct);
        output.WriteLine($"{fen} = {direct}");
        output.WriteLine($"{mirrored} = {reflected}");

        Assert.Equal(direct, reflected);
    }
}
