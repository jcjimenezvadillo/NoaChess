using System.IO;
using System.Windows;

namespace NoaChess.GUI.Wpf.Views;

// Startup modal that renders CHANGELOG.md (copied next to the executable at
// build time) so the user sees the current version and its contents before
// playing. Shown with ShowDialog from MainWindow: it must be closed to play.
public partial class ChangelogDialog : Window
{
    public ChangelogDialog()
    {
        InitializeComponent();

        string path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        MarkdownViewer.Markdown = File.Exists(path)
            ? File.ReadAllText(path)
            : "# NoaChess\n\nCHANGELOG.md not found next to the executable.";
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
