using NoaChess.Core;
using NoaChess.Engine.Search;
using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.ViewModels;

// One completed search iteration, the row an engine output pane is made of:
// depth, evaluation, elapsed time, nodes, speed and the principal variation.
//
// The PV arrives as raw moves, so it is replayed on a copy of the position to
// be written in SAN. That copy is essential: the real board is on screen.
public sealed class AnalysisLineViewModel
{
    public int Depth { get; }
    public string DepthText { get; }
    public string ScoreText { get; }
    public string TimeText { get; }
    public string NodesText { get; }
    public string NpsText { get; }
    public string PvText { get; }

    // White-relative score, kept so the view can colour the row by who stands
    // better without parsing the text back.
    public int WhiteScore { get; }

    public AnalysisLineViewModel(SearchProgress progress, Board position, double seconds)
    {
        Depth = progress.Depth;
        DepthText = progress.Depth.ToString();
        WhiteScore = Formatting.ToWhiteScore(progress.Score, position.SideToMove);
        ScoreText = Formatting.Score(WhiteScore);
        TimeText = Formatting.Time(seconds);
        NodesText = Formatting.Nodes(progress.NodesSearched);
        NpsText = Formatting.Nps(progress.NodesSearched, seconds);
        PvText = WritePv(position, progress.Pv);
    }

    // Replays the variation on a clone to turn it into readable notation.
    // A move that does not fit the position stops the line rather than
    // corrupting it: the PV comes out of a transposition table and a hash
    // collision can put a stale move at the end of it.
    private static string WritePv(Board position, Move[] pv)
    {
        if (pv is null || pv.Length == 0)
            return "";

        Board board = position.Clone();
        var text = new System.Text.StringBuilder();
        int played = 0;

        foreach (Move move in pv)
        {
            if (!MoveGenerator.GenerateLegalMoves(board).Contains(move))
                break;

            if (board.SideToMove == Color.White)
                text.Append(board.FullmoveNumber).Append(". ");
            else if (played == 0)
                text.Append(board.FullmoveNumber).Append("... ");

            text.Append(San.Format(board, move)).Append(' ');
            board.MakeMove(move);
            played++;
        }

        return text.ToString().TrimEnd();
    }
}
