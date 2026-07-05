using System.Windows.Media;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;

namespace NoaChess.GUI.Wpf.ViewModels;

// ViewModel of ONE board square. It is a visual projection of the state of the
// NoaChess.Core Board: it contains no chess rules, only what the view needs to
// paint (piece image, background color, highlights).
public sealed class SquareViewModel(int square, BoardViewModel owner) : ViewModelBase
{
    private ImageSource? _pieceImage;
    private bool _isSelected;
    private bool _isLegalTarget;
    private bool _isLastMove;

    // 0..63 index of the square in the Core Board.
    public int Square { get; } = square;

    // True if the square is light (for the checkered background pattern).
    public bool IsLightSquare { get; } = (Squares.FileOf(square) + Squares.RankOf(square)) % 2 != 0;

    // Vector image of the piece occupying the square, or null if empty.
    // The images come from PieceImageProvider (SVG converted to WPF vectors),
    // so they rescale losslessly with the board.
    public ImageSource? PieceImage
    {
        get => _pieceImage;
        private set => SetProperty(ref _pieceImage, value);
    }

    // The square is selected as the origin of a move.
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // The square is a legal destination of the selected piece (highlighted).
    public bool IsLegalTarget
    {
        get => _isLegalTarget;
        set => SetProperty(ref _isLegalTarget, value);
    }

    // The square was the origin or destination of the last move.
    public bool IsLastMove
    {
        get => _isLastMove;
        set => SetProperty(ref _isLastMove, value);
    }

    // Forwards the user's click to the board ViewModel, which decides what to do.
    public void OnClicked() => owner.OnSquareClicked(this);

    // Syncs the visual content with the actual piece on the Board.
    public void UpdateFromBoard(Board board)
    {
        PieceImage = board.IsEmpty(Square)
            ? null
            : PieceImageProvider.Get(board.ColorAt(Square), board.PieceTypeAt(Square));
    }
}
