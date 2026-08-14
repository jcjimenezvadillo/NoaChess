using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NoaChess.Engine;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.ViewModels;
using NoaChess.GUI.Wpf.Views;

namespace NoaChess.GUI.Wpf;

// Code-behind of the main window. Following MVVM, only view plumbing lives
// here: creating the ViewModel, turning pixel coordinates into board squares,
// and moving the dragged piece with the cursor. No chess or game logic.
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isDragging;

    public MainWindow()
    {
        InitializeComponent();

        // The window title carries the live engine version (single source of
        // truth: ChessEngine.Version, the same constant UCI "id name" uses), so
        // it is always current after a rebuild with nothing to edit by hand.
        Title = $"NoaChess {ChessEngine.Version}";
        VersionBadge.Text = ChessEngine.Version;

        // The promotion selector is injected as a service so the ViewModel does
        // not depend on WPF windows.
        _viewModel = new MainViewModel(new PromotionDialog.Service());
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.NewGameRequested += ShowNewGameDialog;
        _viewModel.OpenPgnRequested += OpenPgn;
        _viewModel.SavePgnRequested += SavePgn;

        // The engine is started here rather than in the ViewModel's constructor
        // because it reports from a worker thread and needs the dispatcher to
        // be running. The release notes wait for the same moment: a modal needs
        // a live Owner to centre on, and they appear only after an upgrade.
        Loaded += (_, _) =>
        {
            if (_viewModel.ConsumeFirstRunOfThisVersion())
                new ChangelogDialog { Owner = this }.ShowDialog();
            _viewModel.Start();
        };

        RestoreGeometry();
        Closing += (_, _) => _viewModel.SaveGeometry(
            RestoreBounds.Width, RestoreBounds.Height,
            RestoreBounds.Left, RestoreBounds.Top,
            WindowState == WindowState.Maximized);

        // The space bar has to reach the command, not whatever button was
        // clicked last. A Button treats Space as a click and HANDLES the key
        // long before the window's input bindings are consulted, so after
        // pressing any toolbar button the space bar silently repeated that
        // button. Tunnelling catches it at the window first, which is the only
        // place that ordering can be won.
        PreviewKeyDown += OnPreviewKeyDown;

        Closed += (_, _) => _viewModel.Dispose();
    }

    // The bare keys that have to work wherever the focus happens to be, the way
    // they do in the established board programs: the arrows walk the game and
    // space plays a move, whether the last thing clicked was a button, the move
    // list or the board itself.
    //
    // They cannot be window input bindings. A Button treats Space as a click
    // and a ListBox treats Left and Right as its own navigation, and both of
    // them HANDLE the key long before the window's bindings are consulted - so
    // after clicking any button the space bar silently repeated that button,
    // and after clicking a move the arrows scrolled the list instead of walking
    // the game. Tunnelling catches the key at the window first, which is the
    // only place that ordering can be won.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Never while typing: a FEN box needs its spaces and its arrows.
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
            return;

        // Only the bare keys are taken. Anything with a modifier is left to the
        // window's input bindings, which nothing steals.
        if (Keyboard.Modifiers != ModifierKeys.None)
            return;

        ICommand? command = e.Key switch
        {
            Key.Space => _viewModel.MoveNowCommand,
            Key.Left => _viewModel.PreviousCommand,
            Key.Right => _viewModel.NextCommand,
            Key.Home => _viewModel.FirstCommand,
            Key.End => _viewModel.LastCommand,
            _ => null,
        };

        if (command is null)
            return;

        if (command.CanExecute(null))
            command.Execute(null);

        // Handled either way. When the command cannot run, the answer is
        // "nothing happens", not "do whatever the focused control would do".
        e.Handled = true;
    }

    // Puts the window back where it was left. A saved position is ignored when
    // it falls outside every screen currently attached: a window restored onto
    // a monitor that has been unplugged is a window the user cannot reach.
    private void RestoreGeometry()
    {
        (double width, double height, double left, double top, bool maximised) =
            _viewModel.SavedGeometry;

        if (width >= MinWidth && height >= MinHeight)
        {
            Width = width;
            Height = height;
        }

        if (!double.IsNaN(left) && !double.IsNaN(top)
            && left + width > SystemParameters.VirtualScreenLeft
            && top + height > SystemParameters.VirtualScreenTop
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (maximised)
            WindowState = WindowState.Maximized;
    }

    // ---- The board as a picture ----

    // Renders the board at twice its logical size, which is what makes the
    // result usable in a document rather than a blurry screenshot.
    private System.Windows.Media.Imaging.BitmapSource RenderBoard()
    {
        const int scale = 2;
        int width = (int)(BoardSurface.Width * scale);
        int height = (int)(BoardSurface.Height * scale);

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
        BoardSurface.UpdateLayout();
        bitmap.Render(BoardSurface);
        return bitmap;
    }

    private void OnCopyBoardClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetImage(RenderBoard());
        }
        catch
        {
            // The clipboard belongs to the whole desktop and another process
            // can be holding it.
        }
    }

    private void OnSaveBoardClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the board",
            Filter = "PNG image (*.png)|*.png",
            DefaultExt = ".png",
            AddExtension = true,
            FileName = "board.png",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(RenderBoard()));
            using var stream = System.IO.File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"The image could not be saved.\n\n{ex.Message}",
                            "Save the board", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnReplaySpeedClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int ms))
            _viewModel.ReplaySpeedMs = ms;
    }

    // ---- Board geometry ----

    // Which square of the board a point lands on, or -1 when it is outside.
    private static int DisplayIndexAt(Point point)
    {
        if (point.X < 0 || point.Y < 0
            || point.X >= BoardViewModel.BoardSize || point.Y >= BoardViewModel.BoardSize)
        {
            return -1;
        }

        int column = (int)(point.X / BoardViewModel.SquareSize);
        int row = (int)(point.Y / BoardViewModel.SquareSize);
        return row * 8 + column;
    }

    private void PositionGhost(Point point)
    {
        // Centred on the cursor: a piece held by its corner feels wrong at once.
        Canvas.SetLeft(DragGhost, point.X - BoardViewModel.SquareSize / 2);
        Canvas.SetTop(DragGhost, point.Y - BoardViewModel.SquareSize / 2);
    }

    // ---- Mouse ----

    private void OnBoardMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point point = e.GetPosition(BoardSurface);
        ImageSource? ghost = _viewModel.Board.BeginInteraction(DisplayIndexAt(point));
        if (ghost is null)
            return; // The press selected nothing, or completed a click-click move.

        DragGhost.Source = ghost;
        DragGhost.Visibility = Visibility.Visible;
        PositionGhost(point);

        _isDragging = true;
        // Capture keeps the release coming to us even if the cursor leaves the
        // board, which is exactly how a piece gets dropped off the edge.
        BoardSurface.CaptureMouse();
    }

    private void OnBoardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;

        Point point = e.GetPosition(BoardSurface);
        PositionGhost(point);
        _viewModel.Board.DragOver(DisplayIndexAt(point));
    }

    private void OnBoardMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging)
            return;

        Point point = e.GetPosition(BoardSurface);
        EndDrag();
        _viewModel.Board.EndInteraction(DisplayIndexAt(point));
    }

    private void OnBoardLostCapture(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            return;
        EndDrag();
        _viewModel.Board.CancelInteraction();
    }

    private void OnBoardMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isDragging)
            _viewModel.Board.DragOver(-1);
    }

    private void EndDrag()
    {
        _isDragging = false;
        DragGhost.Visibility = Visibility.Collapsed;
        DragGhost.Source = null;
        if (BoardSurface.IsMouseCaptured)
            BoardSurface.ReleaseMouseCapture();
    }

    // The right button cancels a queued premove and nothing else.
    private void OnBoardRightDown(object sender, MouseButtonEventArgs e)
        => _viewModel.Board.RightDown();

    // ---- Move list ----

    // True while a click on the list is being handled, so the list is not
    // scrolled underneath the finger that just clicked it.
    private bool _movePickedFromList;

    private void OnMoveCellClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MoveCellViewModel cell })
            return;

        _movePickedFromList = true;
        try
        {
            _viewModel.GoToPlyCommand.Execute(cell);
        }
        finally
        {
            // Cleared after the property change has been dispatched, not here:
            // the scroll handler runs on a later dispatcher pass.
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                                   () => _movePickedFromList = false);
        }
    }

    // Keeps the move being shown inside the visible part of the list, and
    // otherwise LEAVES THE LIST ALONE.
    //
    // This used to jump the scroll to a fraction of the list on every single
    // ply change, which meant that clicking a move you could already see threw
    // the list somewhere else under your hand. Two rules fix that: a move
    // picked from the list never scrolls anything, and a move reached any other
    // way scrolls only when it is off screen, and then by the smallest amount
    // that brings it back.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPly))
            return;

        if (_movePickedFromList)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            if (MoveScroller.ScrollableHeight <= 0)
                return;

            int totalRows = _viewModel.MoveRows.Count;
            int ply = _viewModel.CurrentPly;
            if (totalRows == 0 || ply <= 0)
                return;

            // Rows are uniform, so the extent divided by the count is the row
            // height without having to go looking for the container.
            double rowHeight = MoveScroller.ExtentHeight / totalRows;
            if (rowHeight <= 0)
                return;

            double top = ((ply - 1) / 2) * rowHeight;
            double bottom = top + rowHeight;
            double viewTop = MoveScroller.VerticalOffset;
            double viewBottom = viewTop + MoveScroller.ViewportHeight;

            if (top < viewTop)
                MoveScroller.ScrollToVerticalOffset(top);
            else if (bottom > viewBottom)
                MoveScroller.ScrollToVerticalOffset(bottom - MoveScroller.ViewportHeight);
            // Already visible: the list stays exactly where the user left it.
        });
    }

    // A candidate can be played straight from the list, which is the fastest
    // way to explore one.
    private void OnCandidateClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CandidateMoveViewModel candidate })
            _viewModel.PlayCandidateCommand.Execute(candidate);
    }

    // Clicking a decision jumps the board to it, which is the whole point of
    // knowing where they were.
    private void OnDecisionClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DecisionPointViewModel decision })
            _viewModel.GoToDecisionCommand.Execute(decision);
    }

    // ---- Menu ----

    private void OnExitClicked(object sender, RoutedEventArgs e) => Close();

    // Side and time control are chosen together, the way every chess program
    // asks for them, and only then does the game start.
    private void ShowNewGameDialog()
    {
        var dialog = new NewGameDialog(_viewModel.Mode, _viewModel.TimeControl,
                                       _viewModel.Strength,
                                       _viewModel.WhitePlayer, _viewModel.BlackPlayer,
                                       _viewModel.Catalog.Engines) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _viewModel.StartGame(dialog.SelectedControl, dialog.SelectedStrength,
                                 dialog.SelectedWhite, dialog.SelectedBlack);
        }
    }

    // The board editor starts from whatever is on the board now, which is what
    // makes it useful for fixing up a position rather than only building one.
    private void OnSetupPositionClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new PositionEditorDialog(_viewModel.CurrentFen,
                                              _viewModel.Board.Palette,
                                              _viewModel.Board.IsFlipped) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.SetUpPosition(dialog.ResultFen);
    }

    private void OpenPgn()
    {
        (string? text, string name) = PgnFile.OpenNamed(this);
        if (text is null)
            return;

        // A PGN file usually holds a collection. Opening the first game and
        // saying nothing would quietly hide the rest of the file.
        List<NoaChess.Core.PgnGame> games = NoaChess.Core.Pgn.ParseAll(text)
            .Where(g => g.Moves.Count > 0)
            .ToList();

        if (games.Count == 0)
        {
            MessageBox.Show(this, "No game could be read from that file.",
                            "Open a game", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (games.Count == 1)
        {
            _viewModel.LoadGame(games[0]);
            return;
        }

        var picker = new GamePickerDialog(games, name) { Owner = this };
        if (picker.ShowDialog() == true)
            _viewModel.LoadGame(games[picker.SelectedIndex]);
    }

    private void SavePgn()
    {
        string? path = PgnFile.Save(this, _viewModel.CurrentPgn, _viewModel.SuggestedPgnName);
        if (path is not null)
            _viewModel.ReportSaved(path);
    }

    private void OnThreadsClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int threads))
            _viewModel.Threads = threads;
    }

    private void OnHashClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int megabytes))
            _viewModel.HashMb = megabytes;
    }

    private void OnModeClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse(tag, out GameMode mode))
            _viewModel.SetModeCommand.Execute(mode);
    }

    private void OnPaletteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name })
            _viewModel.SetPaletteByName(name);
    }

    private void OnEnginesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new EnginesDialog(_viewModel.Catalog) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Changed)
            _viewModel.SaveCatalog();
    }

    private void OnGameDetailsClicked(object sender, RoutedEventArgs e)
    {
        // The dialog edits the game's own tag dictionary in place, so there is
        // nothing to copy back.
        var dialog = new GameDetailsDialog(_viewModel.GameTags) { Owner = this };
        if (dialog.ShowDialog() == true)
            _viewModel.RefreshPlayers();
    }

    private void OnReviewDepthClicked(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int depth))
            _viewModel.ReviewDepth = depth;
    }

    private void OnTablebasesClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose the folder holding the Syzygy tables",
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.LoadTablebases(dialog.FolderName);
    }

    private void OnLoadNnueClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load an NNUE network",
            Filter = "NoaChess network (*.noannue)|*.noannue|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
            _viewModel.LoadNnue(dialog.FileName);
    }

    private void OnChangelogClicked(object sender, RoutedEventArgs e)
        => new ChangelogDialog { Owner = this }.ShowDialog();

    private void OnHelpClicked(object sender, RoutedEventArgs e)
        => new HelpDialog { Owner = this }.ShowDialog();
}
