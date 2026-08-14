using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;
using NoaChess.GUI.Wpf.Theme;
using Color = NoaChess.Core.Color;

namespace NoaChess.GUI.Wpf.ViewModels;

// One entry of the piece palette: a piece to stamp onto the board, or the
// eraser (Type = None).
public sealed class PieceStamp(Color color, PieceType type) : ViewModelBase
{
    private bool _isSelected;

    public Color Color { get; } = color;
    public PieceType Type { get; } = type;
    public bool IsEraser => type == PieceType.None;

    public ImageSource? Image { get; } =
        type == PieceType.None ? null : PieceImageProvider.Get(color, type);

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

// The position editor. Pick a piece from the palette, then click squares to
// place it; the right button clears a square. Side to move and castling rights
// are set alongside, and the FEN box is both a read-out and a way in.
//
// It owns a plain 64-square array rather than a Core Board, because half the
// positions passing through an editor are illegal while they are being built -
// no kings yet, two pieces being swapped - and a Board is entitled to refuse
// those. The Core is consulted at the end, to say whether the result is a
// position at all.
public sealed class PositionEditorViewModel : ViewModelBase
{
    private readonly PieceType[] _types = new PieceType[64];
    private readonly Color[] _colors = new Color[64];
    private readonly SquareViewModel[] _squares = new SquareViewModel[64];

    private BoardPalette _palette;
    private bool _isFlipped;
    private bool _whiteToMove = true;
    private bool _whiteKingSide = true, _whiteQueenSide = true;
    private bool _blackKingSide = true, _blackQueenSide = true;
    private string _fen = "";
    private string _problem = "";
    private bool _updatingFromFen;

    public ObservableCollection<SquareViewModel> DisplaySquares { get; } = [];
    public ObservableCollection<PieceStamp> WhitePalette { get; } = [];
    public ObservableCollection<PieceStamp> BlackPalette { get; } = [];
    public PieceStamp Eraser { get; }

    public PositionEditorViewModel(string startFen, BoardPalette palette, bool flipped)
    {
        _palette = palette;
        _isFlipped = flipped;

        for (int sq = 0; sq < 64; sq++)
        {
            _types[sq] = PieceType.None;
            _squares[sq] = new SquareViewModel(sq);
            _squares[sq].ApplyPalette(palette);
        }

        foreach (PieceType type in new[] { PieceType.King, PieceType.Queen, PieceType.Rook,
                                           PieceType.Bishop, PieceType.Knight, PieceType.Pawn })
        {
            WhitePalette.Add(new PieceStamp(Color.White, type));
            BlackPalette.Add(new PieceStamp(Color.Black, type));
        }
        Eraser = new PieceStamp(Color.White, PieceType.None);

        RebuildDisplayOrder();
        LoadFen(startFen);
        Select(WhitePalette[5]); // the pawn: the piece an editor places most
    }

    // ---- Palette ----

    public PieceStamp? Selected { get; private set; }

    public void Select(PieceStamp stamp)
    {
        foreach (PieceStamp p in WhitePalette.Concat(BlackPalette).Append(Eraser))
            p.IsSelected = ReferenceEquals(p, stamp);
        Selected = stamp;
    }

    // ---- Board editing ----

    public int CoreSquareAt(int displayIndex)
    {
        int row = displayIndex / 8, col = displayIndex % 8;
        int rank = _isFlipped ? row : 7 - row;
        int file = _isFlipped ? 7 - col : col;
        return Squares.FromFileRank(file, rank);
    }

    // Stamps the selected piece, or clears the square when the eraser is armed.
    public void Stamp(int displayIndex)
    {
        if (displayIndex < 0 || Selected is null)
            return;

        int square = CoreSquareAt(displayIndex);
        if (Selected.IsEraser)
        {
            Clear(square);
            return;
        }

        // Only one king per side survives: placing a second one moves it,
        // which is what an editor should do rather than refusing the click.
        if (Selected.Type == PieceType.King)
        {
            for (int sq = 0; sq < 64; sq++)
            {
                if (_types[sq] == PieceType.King && _colors[sq] == Selected.Color)
                    Clear(sq);
            }
        }

        _types[square] = Selected.Type;
        _colors[square] = Selected.Color;
        _squares[square].SetPiece(Selected.Color, Selected.Type);
        Recompute();
    }

    public void Erase(int displayIndex)
    {
        if (displayIndex < 0)
            return;
        Clear(CoreSquareAt(displayIndex));
        Recompute();
    }

    private void Clear(int square)
    {
        _types[square] = PieceType.None;
        _squares[square].SetPiece(Color.White, PieceType.None);
    }

    public void ClearBoard()
    {
        for (int sq = 0; sq < 64; sq++)
            Clear(sq);
        Recompute();
    }

    public void LoadStartPosition() => LoadFen(Core.Board.StartFen);

    public void Flip()
    {
        _isFlipped = !_isFlipped;
        RebuildDisplayOrder();
    }

    // ---- Options ----

    public bool WhiteToMove
    {
        get => _whiteToMove;
        set { if (SetProperty(ref _whiteToMove, value)) { OnPropertyChanged(nameof(BlackToMove)); Recompute(); } }
    }

    public bool BlackToMove
    {
        get => !_whiteToMove;
        set { if (value != !_whiteToMove) WhiteToMove = !value; }
    }

    public bool WhiteKingSide
    {
        get => _whiteKingSide;
        set { if (SetProperty(ref _whiteKingSide, value)) Recompute(); }
    }

    public bool WhiteQueenSide
    {
        get => _whiteQueenSide;
        set { if (SetProperty(ref _whiteQueenSide, value)) Recompute(); }
    }

    public bool BlackKingSide
    {
        get => _blackKingSide;
        set { if (SetProperty(ref _blackKingSide, value)) Recompute(); }
    }

    public bool BlackQueenSide
    {
        get => _blackQueenSide;
        set { if (SetProperty(ref _blackQueenSide, value)) Recompute(); }
    }

    // ---- FEN ----

    // Both a read-out and a way in: typing a position here loads it.
    public string Fen
    {
        get => _fen;
        set
        {
            if (!SetProperty(ref _fen, value) || _updatingFromFen)
                return;
            LoadFen(value);
        }
    }

    // Empty when the position is usable. Otherwise it says what is wrong, in
    // the terms a chess player would use.
    public string Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
                OnPropertyChanged(nameof(IsValid));
        }
    }

    public bool IsValid => _problem.Length == 0;

    public void LoadFen(string fen)
    {
        Core.Board board;
        try
        {
            board = new Core.Board(fen.Trim());
        }
        catch
        {
            Problem = "That is not a position NoaChess can read.";
            return;
        }

        for (int sq = 0; sq < 64; sq++)
        {
            _types[sq] = board.PieceTypeAt(sq);
            _colors[sq] = board.IsEmpty(sq) ? Color.White : board.ColorAt(sq);
            _squares[sq].SetPiece(_colors[sq], _types[sq]);
        }

        _whiteToMove = board.SideToMove == Color.White;
        _whiteKingSide = (board.CastlingRights & CastlingRights.WhiteKingSide) != 0;
        _whiteQueenSide = (board.CastlingRights & CastlingRights.WhiteQueenSide) != 0;
        _blackKingSide = (board.CastlingRights & CastlingRights.BlackKingSide) != 0;
        _blackQueenSide = (board.CastlingRights & CastlingRights.BlackQueenSide) != 0;

        OnPropertyChanged(nameof(WhiteToMove));
        OnPropertyChanged(nameof(BlackToMove));
        OnPropertyChanged(nameof(WhiteKingSide));
        OnPropertyChanged(nameof(WhiteQueenSide));
        OnPropertyChanged(nameof(BlackKingSide));
        OnPropertyChanged(nameof(BlackQueenSide));

        Recompute();
    }

    // Rebuilds the FEN from the squares and re-checks whether it describes a
    // legal position. Castling rights are dropped silently when the king or the
    // rook is not home: a FEN claiming a castle that cannot exist is worse than
    // one that quietly tells the truth.
    private void Recompute()
    {
        var text = new StringBuilder(80);

        for (int rank = 7; rank >= 0; rank--)
        {
            int empty = 0;
            for (int file = 0; file < 8; file++)
            {
                int sq = Squares.FromFileRank(file, rank);
                if (_types[sq] == PieceType.None)
                {
                    empty++;
                    continue;
                }
                if (empty > 0)
                {
                    text.Append(empty);
                    empty = 0;
                }
                char c = "pnbrqk"[(int)_types[sq]];
                text.Append(_colors[sq] == Color.White ? char.ToUpperInvariant(c) : c);
            }
            if (empty > 0)
                text.Append(empty);
            if (rank > 0)
                text.Append('/');
        }

        text.Append(_whiteToMove ? " w " : " b ");

        var castling = new StringBuilder(4);
        if (_whiteKingSide && HomeRook(Color.White, 7) && HomeKing(Color.White)) castling.Append('K');
        if (_whiteQueenSide && HomeRook(Color.White, 0) && HomeKing(Color.White)) castling.Append('Q');
        if (_blackKingSide && HomeRook(Color.Black, 7) && HomeKing(Color.Black)) castling.Append('k');
        if (_blackQueenSide && HomeRook(Color.Black, 0) && HomeKing(Color.Black)) castling.Append('q');
        text.Append(castling.Length == 0 ? "-" : castling.ToString());

        // No en passant square is offered: it is only meaningful for one ply
        // and an editor has no way to know which pawn just moved.
        text.Append(" - 0 1");

        _updatingFromFen = true;
        Fen = text.ToString();
        _updatingFromFen = false;

        Problem = Validate(_fen);
    }

    private bool HomeKing(Color color) =>
        _types[Squares.FromFileRank(4, color == Color.White ? 0 : 7)] == PieceType.King
        && _colors[Squares.FromFileRank(4, color == Color.White ? 0 : 7)] == color;

    private bool HomeRook(Color color, int file)
    {
        int sq = Squares.FromFileRank(file, color == Color.White ? 0 : 7);
        return _types[sq] == PieceType.Rook && _colors[sq] == color;
    }

    // Why the position cannot be played, or "" when it can.
    private string Validate(string fen)
    {
        int whiteKings = 0, blackKings = 0;
        for (int sq = 0; sq < 64; sq++)
        {
            if (_types[sq] != PieceType.King)
                continue;
            if (_colors[sq] == Color.White) whiteKings++; else blackKings++;
        }

        if (whiteKings != 1 || blackKings != 1)
            return "Each side needs exactly one king.";

        for (int file = 0; file < 8; file++)
        {
            if (_types[Squares.FromFileRank(file, 0)] == PieceType.Pawn
                || _types[Squares.FromFileRank(file, 7)] == PieceType.Pawn)
            {
                return "A pawn cannot stand on the first or the last rank.";
            }
        }

        Core.Board board;
        try
        {
            board = new Core.Board(fen);
        }
        catch
        {
            return "That is not a position NoaChess can read.";
        }

        // The side that is NOT to move must not be in check: it would mean the
        // previous move left its own king attacked, which cannot have happened.
        Color idle = Core.Board.OppositeColor(board.SideToMove);
        if (board.IsSquareAttacked(board.KingSquare(idle), board.SideToMove))
        {
            return board.SideToMove == Color.White
                ? "Black is in check but it is white to move."
                : "White is in check but it is black to move.";
        }

        return "";
    }

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
                square.RankLabel = col == 0 ? ((char)('1' + rank)).ToString() : null;
                square.FileLabel = row == 7 ? ((char)('a' + file)).ToString() : null;
                DisplaySquares.Add(square);
            }
        }
    }
}
