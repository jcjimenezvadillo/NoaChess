using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NoaChess.GUI.Wpf.Theme;
using NoaChess.GUI.Wpf.ViewModels;

namespace NoaChess.GUI.Wpf.Views;

// Board editor. As in the main window, the only thing living here is turning
// pixel coordinates into squares; what a click means is the ViewModel's answer.
public partial class PositionEditorDialog : Window
{
    private const double SquareSize = 60; // the editor board is 480 units across

    private readonly PositionEditorViewModel _viewModel;

    public PositionEditorDialog(string startFen, BoardPalette palette, bool flipped)
    {
        InitializeComponent();
        _viewModel = new PositionEditorViewModel(startFen, palette, flipped);
        DataContext = _viewModel;
    }

    // The position the user settled on, valid only when the dialog was accepted.
    public string ResultFen => _viewModel.Fen;

    private static int DisplayIndexAt(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X >= 480 || point.Y >= 480)
            return -1;
        return (int)(point.Y / SquareSize) * 8 + (int)(point.X / SquareSize);
    }

    private void OnBoardLeftClick(object sender, MouseButtonEventArgs e)
        => _viewModel.Stamp(DisplayIndexAt(e.GetPosition(EditorSurface)));

    private void OnBoardRightClick(object sender, MouseButtonEventArgs e)
        => _viewModel.Erase(DisplayIndexAt(e.GetPosition(EditorSurface)));

    private void OnStampClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PieceStamp stamp })
            _viewModel.Select(stamp);
    }

    private void OnWhiteToMoveClicked(object sender, RoutedEventArgs e) => _viewModel.WhiteToMove = true;

    private void OnBlackToMoveClicked(object sender, RoutedEventArgs e) => _viewModel.WhiteToMove = false;

    private void OnStartPositionClicked(object sender, RoutedEventArgs e) => _viewModel.LoadStartPosition();

    private void OnClearClicked(object sender, RoutedEventArgs e) => _viewModel.ClearBoard();

    private void OnFlipClicked(object sender, RoutedEventArgs e) => _viewModel.Flip();

    private void OnAcceptClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
