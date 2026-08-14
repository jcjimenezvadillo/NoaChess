using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.ViewModels;

// One legal move of the position on screen, with the score the engine gives it.
//
// This is the answer to "what else was there", which a single best line never
// shows. It is built by searching each move's own subtree, so the numbers are
// comparable with each other: every candidate got the same depth.
public sealed class CandidateMoveViewModel : ViewModelBase
{
    private bool _isBest;

    public int Rank { get; set; }
    public string RankText => $"{Rank}.";

    public NoaChess.Core.Move Move { get; }
    public string San { get; }

    // White-relative, like everything else on screen.
    public int WhiteScore { get; }
    public string ScoreText { get; }

    // How far this move is behind the best one, in pawns. Empty for the best
    // move itself: "0.00 behind" is noise.
    public string BehindText { get; set; } = "";

    // Width of the little bar drawn behind the row, 0..1, relative to the best
    // move. It turns a column of numbers into something readable at a glance.
    public double BarFraction { get; set; }

    public bool IsBest
    {
        get => _isBest;
        set => SetProperty(ref _isBest, value);
    }

    public CandidateMoveViewModel(NoaChess.Core.Move move, string san, int whiteScore)
    {
        Move = move;
        San = san;
        WhiteScore = whiteScore;
        ScoreText = Formatting.Score(whiteScore);
    }
}
