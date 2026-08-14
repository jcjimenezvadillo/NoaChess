using NoaChess.GUI.Wpf.Models;

namespace NoaChess.GUI.Wpf.ViewModels;

// One engine in the catalogue, as the list shows it.
public sealed class EngineEntry(PlayerSetup setup, bool missing) : ViewModelBase
{
    private bool _isSelected;

    public PlayerSetup Setup { get; } = setup;
    public string Name { get; } = setup.Name;
    public string Path { get; } = setup.Path;

    // The file has moved or been deleted since it was added. Worth saying here
    // rather than failing at the start of a game.
    public bool IsMissing { get; } = missing;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
