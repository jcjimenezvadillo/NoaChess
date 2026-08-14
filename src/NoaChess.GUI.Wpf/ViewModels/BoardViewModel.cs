using System.Collections.ObjectModel;
using System.Windows.Media;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.Theme;
using Color = NoaChess.Core.Color; // Disambiguate from System.Windows.Media.Color.

namespace NoaChess.GUI.Wpf.ViewModels;

// What the board needs to know about the game around it. Implemented by
// MainViewModel; kept as an interface so the board owns the interaction and
// nothing else, and asks about everything it is not entitled to decide.
public interface IBoardHost
{
    Board Position { get; }

    // The side to move may be moved by the user right now.
    bool IsInputEnabled { get; }

    // The engine is thinking, so a move entered now is queued as a premove.
    bool IsPremoveEnabled { get; }

    // The colour the user is playing, or null when they play both sides.
    Color? HumanColor { get; }

    void PlayUserMove(Move move);

    PieceType AskPromotion(Color side);
}

// The 8x8 surface and every interaction on it: selecting, dragging, dropping,
// premoves and the annotation arrows.
//
// It owns no chess rule. Which moves are legal is answered by the Core's
// MoveGenerator; whether the user is allowed to move at all is answered by the
// host. What lives here is purely what the user is pointing at.
public sealed class BoardViewModel : ViewModelBase
{
    // Logical size of the board. The view scales it with a Viewbox, so these
    // are the units every arrow and overlay is expressed in.
    public const double SquareSize = 80;
    public const double BoardSize = SquareSize * 8;

    private readonly IBoardHost _host;
    private readonly SquareViewModel[] _squares = new SquareViewModel[64];

    private int _selected = Squares.None;
    private List<Move> _selectionMoves = [];
    private int _dragFrom = Squares.None;
    private int _hover = Squares.None;

    private bool _isFlipped;
    private BoardPalette _palette = BoardPalette.All[0];
    private bool _showCoordinates = true;
    private bool _showLegalMoves = true;
    private ImageSource? _dragImage;

    // The squares in the order the view paints them, top-left to bottom-right
    // of the CURRENT orientation.
    public ObservableCollection<SquareViewModel> DisplaySquares { get; } = [];

    // Arrows for the moves the engine has priced. Only ever populated while
    // analysing: during a game they would be telling the player what to play.
    public ObservableCollection<BoardArrow> Arrows { get; } = [];

    // The move the user has queued while the engine thinks, as core squares.
    public (int From, int To)? Premove { get; private set; }

    // Piece under the cursor while dragging, painted by the overlay.
    public ImageSource? DragImage
    {
        get => _dragImage;
        private set => SetProperty(ref _dragImage, value);
    }

    public bool IsDragging => _dragFrom != Squares.None;

    public bool IsFlipped
    {
        get => _isFlipped;
        set
        {
            if (!SetProperty(ref _isFlipped, value))
                return;
            RebuildDisplayOrder();
            RebuildArrows();
        }
    }

    public BoardPalette Palette
    {
        get => _palette;
        set
        {
            if (!SetProperty(ref _palette, value))
                return;
            foreach (SquareViewModel s in _squares)
                s.ApplyPalette(value);
        }
    }

    public bool ShowCoordinates
    {
        get => _showCoordinates;
        set
        {
            if (SetProperty(ref _showCoordinates, value))
                RebuildDisplayOrder();
        }
    }

    public bool ShowLegalMoves
    {
        get => _showLegalMoves;
        set
        {
            if (!SetProperty(ref _showLegalMoves, value))
                return;
            if (!value)
                ClearTargets();
            else if (_selected != Squares.None)
                MarkTargets();
        }
    }

    public BoardViewModel(IBoardHost host)
    {
        _host = host;
        for (int sq = 0; sq < 64; sq++)
        {
            _squares[sq] = new SquareViewModel(sq);
            _squares[sq].ApplyPalette(_palette);
        }
        RebuildDisplayOrder();
    }

    // ---- Projection between core squares and screen slots ----

    public int CoreSquareAt(int displayIndex)
    {
        int row = displayIndex / 8, col = displayIndex % 8;
        int rank = _isFlipped ? row : 7 - row;
        int file = _isFlipped ? 7 - col : col;
        return Squares.FromFileRank(file, rank);
    }

    public int DisplayIndexOf(int square)
    {
        int file = Squares.FileOf(square), rank = Squares.RankOf(square);
        int row = _isFlipped ? rank : 7 - rank;
        int col = _isFlipped ? 7 - file : file;
        return row * 8 + col;
    }

    // ---- Painting ----

    // Dumps the position onto the squares and repaints the state highlights.
    public void Refresh(Board board, Move? lastMove)
    {
        foreach (SquareViewModel s in _squares)
        {
            s.UpdateFromBoard(board);
            s.IsLastMove = false;
            s.IsCheck = false;
        }

        if (lastMove is { } move && move != Move.None)
        {
            _squares[move.From].IsLastMove = true;
            _squares[move.To].IsLastMove = true;
        }

        if (board.IsInCheck())
            _squares[board.KingSquare(board.SideToMove)].IsCheck = true;

        // A selection made in the previous position means nothing in this one,
        // and neither do arrows drawn for it.
        ClearSelection();
        ClearCandidateArrows();
        RefreshPremoveHighlight();
    }

    // ---- Left button: select, drag, drop ----

    // Handles the press. Returns the image to drag when the press picked up a
    // piece, or null when it did something else (completed a click-click move,
    // or cleared the selection).
    public ImageSource? BeginInteraction(int displayIndex)
    {
        if (displayIndex < 0)
        {
            ClearSelection();
            return null;
        }

        int square = CoreSquareAt(displayIndex);

        // Second click of a click-click move: the press lands on a destination
        // the selected piece can reach.
        if (_selected != Squares.None && TryPlayFromSelection(square))
            return null;

        if (!CanPickUp(square))
        {
            ClearSelection();
            return null;
        }

        Select(square);
        _dragFrom = square;
        _squares[square].IsPieceHidden = true;
        DragImage = _squares[square].PieceImage;
        return DragImage;
    }

    // Updates the square the cursor is over during a drag.
    public void DragOver(int displayIndex)
    {
        int square = displayIndex < 0 ? Squares.None : CoreSquareAt(displayIndex);
        if (square == _hover)
            return;

        if (_hover != Squares.None)
            _squares[_hover].IsDragHover = false;
        _hover = square;
        if (_hover != Squares.None && _hover != _dragFrom)
            _squares[_hover].IsDragHover = true;
    }

    // Handles the release. A release on the square the drag started from is a
    // plain click: the selection stays so the user can finish with a second
    // click. A release anywhere else is a drop and either plays or cancels.
    public void EndInteraction(int displayIndex)
    {
        if (_dragFrom == Squares.None)
            return;

        int origin = _dragFrom;
        _dragFrom = Squares.None;
        DragImage = null;
        _squares[origin].IsPieceHidden = false;
        DragOver(-1);

        if (displayIndex < 0)
        {
            ClearSelection();
            return;
        }

        int square = CoreSquareAt(displayIndex);
        if (square == origin)
            return; // A click, not a drag: keep the piece selected.

        if (!TryPlayFromSelection(square))
            ClearSelection();
    }

    // Abandons a drag without playing anything (the window lost the mouse).
    public void CancelInteraction()
    {
        if (_dragFrom != Squares.None)
        {
            _squares[_dragFrom].IsPieceHidden = false;
            _dragFrom = Squares.None;
            DragImage = null;
        }
        DragOver(-1);
        ClearSelection();
    }

    // ---- Candidate arrows ----

    // The moves to draw, best first, as core squares with the centipawns each
    // one gives up against the best. Stored rather than drawn directly so that
    // flipping the board can redraw them without asking the engine again.
    private (int From, int To, int Loss)[] _candidateArrows = [];

    public void SetCandidateArrows(IEnumerable<(int From, int To, int Loss)> moves)
    {
        _candidateArrows = moves.ToArray();
        RebuildArrows();
    }

    public void ClearCandidateArrows()
    {
        if (_candidateArrows.Length == 0)
            return;
        _candidateArrows = [];
        RebuildArrows();
    }

    private void RebuildArrows()
    {
        Arrows.Clear();

        // Drawn worst first so the best one ends up on top of the pile where
        // the arrows overlap.
        for (int i = _candidateArrows.Length - 1; i >= 0; i--)
        {
            (int from, int to, int loss) = _candidateArrows[i];
            Arrows.Add(BoardArrow.Build(DisplayIndexOf(from), DisplayIndexOf(to), SquareSize,
                                        BoardArrow.ColourForLoss(loss),
                                        weight: i == 0 ? 1.15 : 0.85));
        }
    }

    // ---- Right button ----

    // Cancels the queued premove. That is all the right button does on the
    // board.
    public void RightDown() => ClearPremove();

    // ---- Premoves ----

    public void ClearPremove()
    {
        if (Premove is null)
            return;
        Premove = null;
        RefreshPremoveHighlight();
    }

    // Tries to turn the queued premove into a real move now that it is the
    // user's turn. An illegal one is simply dropped, which is what makes
    // premoving safe to use: the worst case is that nothing happens.
    public Move ConsumePremove(Board board)
    {
        if (Premove is not { } pending)
            return Move.None;

        ClearPremove();

        List<Move> candidates = MoveGenerator.GenerateLegalMoves(board)
            .Where(m => m.From == pending.From && m.To == pending.To)
            .ToList();
        if (candidates.Count == 0)
            return Move.None;

        // A premoved promotion is not worth interrupting the user for: it takes
        // the queen, the choice in more than 95% of games.
        return candidates.FirstOrDefault(m => !m.IsPromotion || m.PromotionPiece == PieceType.Queen,
                                         candidates[0]);
    }

    private void RefreshPremoveHighlight()
    {
        foreach (SquareViewModel s in _squares)
            s.IsPremove = false;
        if (Premove is { } p)
        {
            _squares[p.From].IsPremove = true;
            _squares[p.To].IsPremove = true;
        }
    }

    // ---- Selection ----

    private bool CanPickUp(int square)
    {
        Board board = _host.Position;
        if (board.IsEmpty(square))
            return false;

        Color color = board.ColorAt(square);
        if (_host.IsInputEnabled)
            return color == board.SideToMove && (_host.HumanColor is null || color == _host.HumanColor);

        // Not our turn: the only thing that can be picked up is one of our own
        // pieces, to be queued as a premove.
        return _host.IsPremoveEnabled && _host.HumanColor == color;
    }

    private void Select(int square)
    {
        ClearSelection();
        _selected = square;
        _squares[square].IsSelected = true;

        _selectionMoves = _host.IsInputEnabled
            ? MoveGenerator.GenerateLegalMoves(_host.Position).Where(m => m.From == square).ToList()
            : [];

        if (_showLegalMoves)
            MarkTargets();
    }

    private void MarkTargets()
    {
        Board board = _host.Position;
        if (_host.IsInputEnabled)
        {
            foreach (Move m in _selectionMoves)
            {
                _squares[m.To].IsLegalTarget = true;
                _squares[m.To].IsCaptureTarget = m.IsCapture;
            }
            return;
        }

        // Premove candidates: where the piece could go if the position were
        // its own to command. It is a guess by definition - the opponent has
        // not moved yet - so it is drawn with the same hints and validated for
        // real when the move is finally played.
        ulong targets = PremoveTargets(board, _selected);
        while (targets != 0)
        {
            int to = Bitboard.PopLsb(ref targets);
            _squares[to].IsLegalTarget = true;
            _squares[to].IsCaptureTarget = !board.IsEmpty(to);
        }
    }

    private void ClearTargets()
    {
        foreach (SquareViewModel s in _squares)
        {
            s.IsLegalTarget = false;
            s.IsCaptureTarget = false;
        }
    }

    private void ClearSelection()
    {
        if (_selected != Squares.None)
            _squares[_selected].IsSelected = false;
        _selected = Squares.None;
        _selectionMoves = [];
        ClearTargets();
    }

    // Plays (or queues) the move from the current selection to 'square'.
    // Returns false when that square is not a destination at all.
    private bool TryPlayFromSelection(int square)
    {
        if (_selected == Squares.None)
            return false;

        if (!_host.IsInputEnabled)
        {
            // Queue it. Whether it is really playable is decided later, against
            // the position that actually arrives.
            if ((PremoveTargets(_host.Position, _selected) & (1UL << square)) == 0)
                return false;
            Premove = (_selected, square);
            ClearSelection();
            RefreshPremoveHighlight();
            return true;
        }

        List<Move> candidates = _selectionMoves.Where(m => m.To == square).ToList();
        if (candidates.Count == 0)
            return false;

        Move move = candidates[0];
        if (move.IsPromotion)
        {
            PieceType chosen = _host.AskPromotion(_host.Position.SideToMove);
            move = candidates.First(m => m.PromotionPiece == chosen);
        }

        ClearSelection();
        _host.PlayUserMove(move);
        return true;
    }

    // Squares a piece could plausibly reach if it were its side's turn. Used
    // only to draw premove hints, so it deliberately ignores check, pins and
    // whatever the opponent is about to do.
    //
    // SQUARES HOLDING OUR OWN PIECES ARE INCLUDED, and that is not an oversight.
    // The premove that matters most is the RECAPTURE, and when it is entered the
    // target still holds our own piece precisely because the opponent has not
    // taken it yet. Excluding those squares removed the main reason to premove
    // at all. Being generous costs nothing: a premove that turns out to be
    // illegal is discarded when the turn arrives, which is the contract anyway.
    private static ulong PremoveTargets(Board board, int from)
    {
        if (board.IsEmpty(from))
            return 0;

        Color side = board.ColorAt(from);
        PieceType type = board.PieceTypeAt(from);
        ulong occupancy = board.AllOccupancy;

        ulong targets = type switch
        {
            PieceType.Knight => Attacks.Knight(from),
            PieceType.Bishop => Attacks.Bishop(from, occupancy),
            PieceType.Rook => Attacks.Rook(from, occupancy),
            PieceType.Queen => Attacks.Queen(from, occupancy),
            PieceType.King => KingPremoveTargets(board, side, from),
            _ => PawnPremoveTargets(side, from),
        };

        // The square it already stands on is the only one that is never a move.
        return targets & ~(1UL << from);
    }

    private static ulong KingPremoveTargets(Board board, Color side, int from)
    {
        ulong targets = Attacks.King(from);

        // Castling: only offered from the king's home square, and only while
        // the rights are still there.
        int homeRank = side == Color.White ? 0 : 7;
        if (Squares.FileOf(from) == 4 && Squares.RankOf(from) == homeRank)
        {
            CastlingRights kingSide = side == Color.White
                ? CastlingRights.WhiteKingSide : CastlingRights.BlackKingSide;
            CastlingRights queenSide = side == Color.White
                ? CastlingRights.WhiteQueenSide : CastlingRights.BlackQueenSide;
            if ((board.CastlingRights & kingSide) != 0)
                targets |= 1UL << (from + 2);
            if ((board.CastlingRights & queenSide) != 0)
                targets |= 1UL << (from - 2);
        }
        return targets;
    }

    private static ulong PawnPremoveTargets(Color side, int from)
    {
        ulong targets = Attacks.Pawn(side, from);
        int rank = Squares.RankOf(from);
        int step = side == Color.White ? 8 : -8;
        int startRank = side == Color.White ? 1 : 6;

        if (rank + (side == Color.White ? 1 : -1) is >= 0 and <= 7)
            targets |= 1UL << (from + step);
        if (rank == startRank)
            targets |= 1UL << (from + 2 * step);
        return targets;
    }

    // ---- Layout ----

    private void RebuildDisplayOrder()
    {
        DisplaySquares.Clear();
        for (int row = 0; row < 8; row++)
        {
            for (int col = 0; col < 8; col++)
            {
                int rank = _isFlipped ? row : 7 - row;
                int file = _isFlipped ? 7 - col : col;
                SquareViewModel square = _squares[Squares.FromFileRank(file, rank)];

                // Only the outer edge of the CURRENT orientation is labelled:
                // ranks down the left column, files along the bottom row.
                square.RankLabel = _showCoordinates && col == 0 ? ((char)('1' + rank)).ToString() : null;
                square.FileLabel = _showCoordinates && row == 7 ? ((char)('a' + file)).ToString() : null;

                DisplaySquares.Add(square);
            }
        }
    }
}
