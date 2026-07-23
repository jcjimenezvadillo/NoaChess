using System.Text;
using NoaChess.Core;
using NoaChess.Engine.Evaluation.Classical;
using Xunit;

namespace NoaChess.Engine.Tests;

// Color symmetry: for any position P and its vertical mirror P' (ranks
// flipped, colors swapped, side to move swapped), the evaluation must
// satisfy eval(P) == eval(P'). Evaluate() is side-to-move relative, so a
// mirrored position with the mirrored side to move must produce the exact
// same score. Any violation means some term treats White and Black
// differently — a color/sign/shift bug.
public class EvalSymmetryTests
{
    private static int Eval(string fen)
    {
        var board = new Board(fen);
        return new ClassicalEvaluator().Evaluate(board);
    }

    // Mirror a FEN vertically: reverse rank order, swap piece case, swap the
    // side to move, mirror castling rights. En passant square rank-flips.
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
            // Keep KQkq ordering after swapping case.
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
    // Startpos.
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    // Open middlegame, minors developed, outpost-ish knights.
    [InlineData("r1bq1rk1/pp3ppp/2n1pn2/2bp4/4P3/2NP1N2/PPP1BPPP/R1BQ1RK1 w - - 0 9")]
    // Knight on a protected outpost (e5/d4 structures both sides).
    [InlineData("4k3/8/8/4N3/3P4/8/8/4K3 w - - 0 1")]
    // Trapped rook corner structures.
    [InlineData("4k3/8/8/8/8/8/6PP/6KR w - - 0 1")]
    // Bishop long diagonal + bishop pawns.
    [InlineData("4k3/8/8/8/8/8/4P1B1/4K3 w - - 0 1")]
    // Queen x-rayed by rook (WeakQueen).
    [InlineData("3r2k1/8/8/8/3Q4/8/8/4K3 w - - 0 1")]
    // Closed center, rooks on closed files.
    [InlineData("2kr3r/pppq1ppp/2np1n2/4p3/4P3/2NP1N2/PPPQ1PPP/2KR3R w - - 0 10")]
    // Endgame: rook + minor each.
    [InlineData("6k1/5pp1/4b3/8/8/4B3/5PP1/6K1 w - - 0 40")]
    // Uncontested outpost knight on the flank.
    [InlineData("7k/8/8/1N6/2P5/8/5PPP/6K1 w - - 0 40")]
    // Complex position with queens, castling rights intact.
    [InlineData("r2qk2r/ppp2ppp/2npbn2/2b1p3/2B1P3/2NPBN2/PPP2PPP/R2QK2R w KQkq - 0 8")]
    public void Evaluation_IsColorSymmetric(string fen)
    {
        string mirrored = MirrorFen(fen);
        int e1 = Eval(fen);
        int e2 = Eval(mirrored);
        Assert.True(e1 == e2,
            $"eval asymmetry: '{fen}' = {e1}, mirrored '{mirrored}' = {e2}");
    }
}
