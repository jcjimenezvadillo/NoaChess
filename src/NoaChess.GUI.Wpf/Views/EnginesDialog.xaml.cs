using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NoaChess.GUI.Wpf.Models;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.ViewModels;

namespace NoaChess.GUI.Wpf.Views;

// Manages the list of external UCI engines.
public partial class EnginesDialog : Window
{
    private readonly EngineCatalog _catalog;
    private List<EngineEntry> _rows = [];

    public EnginesDialog(EngineCatalog catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        Refresh();
    }

    // True when anything was added or removed, so the caller knows to save.
    public bool Changed { get; private set; }

    private void Refresh()
    {
        _rows = _catalog.Engines
            .Select(e => new EngineEntry(e, !_catalog.Exists(e)))
            .ToList();
        EngineList.ItemsSource = _rows;

        StatusLine.Text = _rows.Count == 0
            ? "No engines yet."
            : $"{_rows.Count} engine{(_rows.Count == 1 ? "" : "s")}.";
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EngineEntry row })
            return;
        foreach (EngineEntry other in _rows)
            other.IsSelected = ReferenceEquals(other, row);
    }

    // Adding starts the program and waits for it to identify itself. A path
    // that does not answer "uciok" is not added: the catalogue is a list of
    // engines, not of hopeful guesses.
    private async void OnAddClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a UCI engine",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        StatusLine.Text = "Starting it to see whether it is an engine...";

        (UciEngine? engine, string error) = await UciEngine.StartAsync(dialog.FileName);
        if (engine is null)
        {
            StatusLine.Text = error;
            return;
        }

        string name = engine.Name.Length > 0
            ? engine.Name
            : System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);

        // It has proved itself; it is not needed again until it plays.
        engine.Dispose();

        _catalog.Add(PlayerSetup.External(name, dialog.FileName));
        Changed = true;
        Refresh();
        StatusLine.Text = $"Added {name}.";
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        EngineEntry? selected = _rows.FirstOrDefault(r => r.IsSelected);
        if (selected is null)
        {
            StatusLine.Text = "Pick an engine from the list first.";
            return;
        }

        _catalog.Remove(selected.Setup);
        Changed = true;
        Refresh();
        StatusLine.Text = $"Removed {selected.Name}.";
    }
}
