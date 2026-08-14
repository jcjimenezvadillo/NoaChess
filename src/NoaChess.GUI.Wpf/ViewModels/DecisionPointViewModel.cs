using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.ViewModels;

// One position where the choice mattered, as the panel shows it.
public sealed class DecisionPointViewModel
{
    public int Ply { get; }
    public string MoveText { get; }
    public string PlayedText { get; }
    public string SpreadText { get; }
    public string TimeText { get; }
    public bool TookIt { get; }

    // Share of the widest decision in the game, for the bar behind the row.
    public double BarFraction { get; set; }

    public DecisionPointViewModel(DecisionPoint point)
    {
        Ply = point.Ply;
        MoveText = point.WhiteToMove ? $"{point.MoveNumber}." : $"{point.MoveNumber}...";
        TookIt = point.TookIt;

        PlayedText = point.TookIt
            ? point.Played
            : $"{point.Played}  (best {point.Best})";

        SpreadText = $"{point.Spread / 100.0:0.0}";

        // A game read from a PGN has no times, and inventing "0.0s" for every
        // move would be worse than saying nothing.
        TimeText = point.Seconds > 0.05 ? $"{point.Seconds:0.0}s" : "";
    }
}
