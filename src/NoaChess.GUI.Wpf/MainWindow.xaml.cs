using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoaChess.GUI.Wpf.ViewModels;
using NoaChess.GUI.Wpf.Views;

namespace NoaChess.GUI.Wpf;

// Code-behind of the main window. Following MVVM, only view "plumbing" lives
// here: creating the ViewModel and translating mouse/button events into
// ViewModel calls. No chess or game logic lives in this class.
public partial class MainWindow : Window
{
    private readonly BoardViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        // The promotion selector is injected as a service so the ViewModel
        // does not depend on WPF windows.
        _viewModel = new BoardViewModel(new PromotionDialog.Service());
        DataContext = _viewModel;

        // Show the changelog modal once the window is up (it needs a live
        // Owner to center on). Being modal, the board is only reachable
        // after closing it — the user always sees what version this is.
        Loaded += (_, _) => new ChangelogDialog { Owner = this }.ShowDialog();
    }

    private void OnNewGameClicked(object sender, RoutedEventArgs e) => _viewModel.NewGame();

    private void OnFlipBoardClicked(object sender, RoutedEventArgs e) => _viewModel.FlipBoard();

    private void OnSquareMouseDown(object sender, MouseButtonEventArgs e)
    {
        // The DataContext of the clicked Border is that square's SquareViewModel.
        if (sender is Border { DataContext: SquareViewModel square })
            square.OnClicked();
    }
}
