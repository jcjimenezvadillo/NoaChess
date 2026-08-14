using System.Windows;
using System.Windows.Input;
using NoaChess.Core;
using NoaChess.GUI.Wpf.ViewModels;

namespace NoaChess.GUI.Wpf.Views;

// Lets the user pick one game out of a PGN collection.
public partial class GamePickerDialog : Window
{
    private readonly List<PgnGameSummary> _rows;

    public GamePickerDialog(IReadOnlyList<PgnGame> games, string fileName)
    {
        InitializeComponent();

        _rows = games.Select((game, index) => new PgnGameSummary(game, index)).ToList();
        GameList.ItemsSource = _rows;
        CountLine.Text = $"{games.Count} games in {fileName}";

        if (_rows.Count > 0)
            Select(_rows[0]);
    }

    // Index of the chosen game, valid only when the dialog was accepted.
    public int SelectedIndex { get; private set; }

    private void Select(PgnGameSummary row)
    {
        foreach (PgnGameSummary other in _rows)
            other.IsSelected = ReferenceEquals(other, row);
        SelectedIndex = row.Index;
    }

    // One click selects, two open it. A Border has no MouseDoubleClick of its
    // own - that belongs to Control - so the count comes off the event itself.
    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PgnGameSummary row })
            return;

        Select(row);
        if (e.ClickCount >= 2)
            DialogResult = true;
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
