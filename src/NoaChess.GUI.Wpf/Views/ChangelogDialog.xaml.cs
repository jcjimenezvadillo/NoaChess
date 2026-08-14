using System.IO;
using System.Windows;
using NoaChess.Engine;

namespace NoaChess.GUI.Wpf.Views;

// Renders CHANGELOG.md (copied next to the executable at build time). Opened
// from Help, and once automatically after an upgrade so a new build introduces
// itself exactly once.
public partial class ChangelogDialog : Window
{
    public ChangelogDialog()
    {
        InitializeComponent();

        VersionLine.Text = $"NoaChess {ChessEngine.Version}";

        string path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        MarkdownViewer.Markdown = File.Exists(path)
            ? File.ReadAllText(path)
            : "# NoaChess\n\nCHANGELOG.md was not found next to the executable.";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
