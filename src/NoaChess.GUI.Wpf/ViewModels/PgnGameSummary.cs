using NoaChess.Core;

namespace NoaChess.GUI.Wpf.ViewModels;

// One row of the game picker: enough of a game to recognise it without loading
// it.
public sealed class PgnGameSummary : ViewModelBase
{
    private bool _isSelected;

    public int Index { get; }
    public string Number { get; }
    public string Players { get; }
    public string Result { get; }
    public string MoveCount { get; }
    public string Event { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public PgnGameSummary(PgnGame game, int index)
    {
        Index = index;
        Number = (index + 1).ToString();

        string white = game.Get("White", "?");
        string black = game.Get("Black", "?");
        Players = $"{white}  -  {black}";

        Result = game.Result;

        // Plies to full moves, which is how a player counts them.
        MoveCount = ((game.Moves.Count + 1) / 2).ToString();

        string name = game.Get("Event", "");
        string date = game.Get("Date", "");
        Event = name.Length > 0 && date.Length > 0 ? $"{name}, {date}"
              : name.Length > 0 ? name
              : date;
    }
}
