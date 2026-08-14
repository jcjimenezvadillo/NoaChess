using System.Windows.Media;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.Theme;
using Color = NoaChess.Core.Color; // Disambiguate from System.Windows.Media.Color.

namespace NoaChess.GUI.Wpf.ViewModels;

// ViewModel of ONE board square: a visual projection of the Core Board plus the
// highlight layers painted over it. It holds no chess rule - what is legal,
// what is check and what the last move was are all decided elsewhere and
// pushed in here.
//
// The highlights are independent booleans rather than one "state" enum because
// they genuinely stack: the destination of the last move can be selected, a
// legal target and in check at the same time, and each draws its own layer.
public sealed class SquareViewModel(int square) : ViewModelBase
{
    private SolidColorBrush _baseBrush = BoardPalette.All[0].Light;
    private SolidColorBrush _labelBrush = BoardPalette.All[0].Dark;
    private ImageSource? _pieceImage;
    private bool _isSelected;
    private bool _isLegalTarget;
    private bool _isCaptureTarget;
    private bool _isLastMove;
    private bool _isPremove;
    private bool _isCheck;
    private bool _isDragHover;
    private bool _isPieceHidden;
    private string? _fileLabel;
    private string? _rankLabel;

    // 0..63 index of the square in the Core Board.
    public int Square { get; } = square;

    // True if the square is light, which fixes which palette colour it takes.
    public bool IsLightSquare { get; } = (Squares.FileOf(square) + Squares.RankOf(square)) % 2 != 0;

    public SolidColorBrush BaseBrush
    {
        get => _baseBrush;
        private set => SetProperty(ref _baseBrush, value);
    }

    public SolidColorBrush LabelBrush
    {
        get => _labelBrush;
        private set => SetProperty(ref _labelBrush, value);
    }

    // Vector image of the piece on the square, or null when it is empty.
    public ImageSource? PieceImage
    {
        get => _pieceImage;
        private set => SetProperty(ref _pieceImage, value);
    }

    // Hidden while its piece is being dragged: the dragged piece is painted by
    // the overlay that follows the cursor, and leaving the original visible too
    // would show the same piece twice.
    public bool IsPieceHidden
    {
        get => _isPieceHidden;
        set
        {
            if (SetProperty(ref _isPieceHidden, value))
                OnPropertyChanged(nameof(PieceOpacity));
        }
    }

    // Bound instead of Visibility so the square keeps its layout while the
    // piece is in the air.
    public double PieceOpacity => _isPieceHidden ? 0 : 1;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // A legal destination of the selected piece, shown as a dot on an empty
    // square and as a ring around the victim on an occupied one.
    public bool IsLegalTarget
    {
        get => _isLegalTarget;
        set
        {
            if (SetProperty(ref _isLegalTarget, value))
                OnHintChanged();
        }
    }

    public bool IsCaptureTarget
    {
        get => _isCaptureTarget;
        set
        {
            if (SetProperty(ref _isCaptureTarget, value))
                OnHintChanged();
        }
    }

    // The two shapes are exposed separately because a dot and a ring are
    // different marks, and a view cannot combine two booleans in a binding.
    public bool ShowMoveDot => _isLegalTarget && !_isCaptureTarget;

    public bool ShowCaptureRing => _isLegalTarget && _isCaptureTarget;

    private void OnHintChanged()
    {
        OnPropertyChanged(nameof(ShowMoveDot));
        OnPropertyChanged(nameof(ShowCaptureRing));
    }

    public bool IsLastMove
    {
        get => _isLastMove;
        set => SetProperty(ref _isLastMove, value);
    }

    // Part of the move the user has queued up while the engine is thinking.
    public bool IsPremove
    {
        get => _isPremove;
        set => SetProperty(ref _isPremove, value);
    }

    // The king of the side to move is standing here and is in check.
    public bool IsCheck
    {
        get => _isCheck;
        set => SetProperty(ref _isCheck, value);
    }

    // The cursor is over this square during a drag.
    public bool IsDragHover
    {
        get => _isDragHover;
        set => SetProperty(ref _isDragHover, value);
    }

    // Coordinate labels. Only the squares on the edge of the CURRENT
    // orientation carry one, so both are recomputed when the board flips.
    public string? FileLabel
    {
        get => _fileLabel;
        set => SetProperty(ref _fileLabel, value);
    }

    public string? RankLabel
    {
        get => _rankLabel;
        set => SetProperty(ref _rankLabel, value);
    }

    public void ApplyPalette(BoardPalette palette)
    {
        BaseBrush = IsLightSquare ? palette.Light : palette.Dark;
        LabelBrush = IsLightSquare ? palette.LightLabel : palette.DarkLabel;
    }

    // Syncs the piece with the actual board contents.
    public void UpdateFromBoard(Board board)
    {
        PieceImage = board.IsEmpty(Square)
            ? null
            : PieceImageProvider.Get(board.ColorAt(Square), board.PieceTypeAt(Square));
    }

    // Sets the piece directly. Used by the position editor, which works on a
    // plain array rather than a Board: half the positions passing through an
    // editor are illegal while they are being built.
    public void SetPiece(Color color, PieceType type) =>
        PieceImage = type == PieceType.None ? null : PieceImageProvider.Get(color, type);
}
