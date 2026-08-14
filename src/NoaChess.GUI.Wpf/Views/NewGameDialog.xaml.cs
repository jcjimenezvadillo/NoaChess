using System.Windows;
using NoaChess.GUI.Wpf.Models;
using NoaChess.GUI.Wpf.ViewModels;

namespace NoaChess.GUI.Wpf.Views;

// Asks for the side and the time control before a game starts. It decides
// nothing itself: the answers go back to the caller, which starts the game.
public partial class NewGameDialog : Window
{
    private readonly NewGameViewModel _viewModel;

    public NewGameDialog(GameMode mode, TimeControl control, EngineStrength strength,
                         PlayerSetup white, PlayerSetup black,
                         IReadOnlyList<PlayerSetup> externals)
    {
        InitializeComponent();
        _viewModel = new NewGameViewModel(mode, control, strength, white, black, externals);
        DataContext = _viewModel;
    }

    public PlayerSetup SelectedWhite => _viewModel.White;

    public PlayerSetup SelectedBlack => _viewModel.Black;

    public GameMode SelectedMode => _viewModel.Mode;

    public TimeControl SelectedControl => _viewModel.Control;

    public EngineStrength SelectedStrength => _viewModel.Strength;

    private void OnStartClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
