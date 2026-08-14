using System.Windows;

namespace NoaChess.GUI.Wpf.Views;

// Edits the PGN tag pairs of the current game. It reads and writes the very
// dictionary the game carries, so what is saved to a file is what was typed
// here, with no second copy to fall out of step.
public partial class GameDetailsDialog : Window
{
    private readonly Dictionary<string, string> _tags;

    public GameDetailsDialog(Dictionary<string, string> tags)
    {
        InitializeComponent();
        _tags = tags;

        WhiteBox.Text = Read("White");
        BlackBox.Text = Read("Black");
        EventBox.Text = Read("Event");
        SiteBox.Text = Read("Site");
        DateBox.Text = Read("Date", DateTime.Now.ToString("yyyy.MM.dd"));
        RoundBox.Text = Read("Round");
    }

    private string Read(string tag, string fallback = "")
        => _tags.TryGetValue(tag, out string? value) && value != "?" ? value : fallback;

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        Write("White", WhiteBox.Text);
        Write("Black", BlackBox.Text);
        Write("Event", EventBox.Text);
        Write("Site", SiteBox.Text);
        Write("Date", DateBox.Text);
        Write("Round", RoundBox.Text);
        DialogResult = true;
    }

    // An emptied field becomes "?", which is what PGN uses for "not known".
    // Leaving the tag out entirely would make the file invalid.
    private void Write(string tag, string value)
        => _tags[tag] = string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
}
