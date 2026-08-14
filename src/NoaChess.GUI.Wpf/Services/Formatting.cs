using NoaChess.Engine.Search;

namespace NoaChess.GUI.Wpf.Services;

// Shared number formatting for the status bar and the engine output pane, so a
// value never appears in two different shapes in the same window.
public static class Formatting
{
    // Anything above this is a forced mate rather than a centipawn evaluation.
    public const int MateBound = AlphaBetaSearch.MateScore - 1000;

    public static bool IsMate(int score) => Math.Abs(score) > MateBound;

    // Evaluation as the whole chess world writes it: pawns with two decimals
    // and an explicit sign, or "#N" for a forced mate N moves away. The score
    // must already be white-relative.
    public static string Score(int whiteScore)
    {
        if (IsMate(whiteScore))
        {
            int plies = AlphaBetaSearch.MateScore - Math.Abs(whiteScore);
            int moves = (plies + 1) / 2;
            return whiteScore > 0 ? $"#{moves}" : $"-#{moves}";
        }
        return (whiteScore / 100.0).ToString("+0.00;-0.00;0.00");
    }

    // Turns a score reported by the search (relative to the side to move at the
    // root) into the white-relative number everything on screen uses.
    public static int ToWhiteScore(int score, NoaChess.Core.Color sideToMove)
        => sideToMove == NoaChess.Core.Color.White ? score : -score;

    // Node counts get long fast, so they are abbreviated the way engine output
    // panes do it rather than printed in full.
    public static string Nodes(long nodes) => nodes switch
    {
        < 1_000 => nodes.ToString(),
        < 1_000_000 => $"{nodes / 1000.0:0.0}k",
        < 1_000_000_000 => $"{nodes / 1_000_000.0:0.00}M",
        _ => $"{nodes / 1_000_000_000.0:0.00}G",
    };

    public static string Nps(long nodes, double seconds)
    {
        if (seconds <= 0.0001)
            return "-";
        long nps = (long)(nodes / seconds);
        return nps < 1_000_000 ? $"{nps / 1000.0:0} kN/s" : $"{nps / 1_000_000.0:0.00} MN/s";
    }

    public static string Time(double seconds) =>
        seconds < 60 ? $"{seconds:0.0}s" : $"{(int)(seconds / 60)}:{seconds % 60:00.0}";
}
