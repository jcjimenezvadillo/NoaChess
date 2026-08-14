using System.Windows.Media;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Models;
using NoaChess.GUI.Wpf.Services;
using Color = NoaChess.Core.Color; // Disambiguate from System.Windows.Media.Color.

namespace NoaChess.GUI.Wpf.ViewModels;

// One move in the move list, written the way printed chess books do it: the
// piece as a figurine instead of a letter.
//
// The SAN is split into up to four visual parts because a promotion needs a
// figurine at the END as well as the possibility of one at the start:
//
//   Nxe5+     -> [N] "xe5+"
//   exd8=Q#   ->     "exd8"  [Q] "#"
//   O-O                      "O-O"
//
// The figurines always use the WHITE piece set regardless of who moved. The
// column already says whose move it is, and a black figurine on a dark panel
// would be a silhouette on a silhouette.
public sealed class MoveCellViewModel : ViewModelBase
{
    private bool _isCurrent;
    private string _annotation = "";
    private Brush _annotationBrush = Brushes.Transparent;

    // Ply this cell leads to: jumping here means moving the game cursor to it.
    public int Ply { get; }

    public ImageSource? LeadFigurine { get; }
    public string Text { get; } = "";
    public ImageSource? TrailFigurine { get; }
    public string Suffix { get; } = "";

    // True when the board is showing the position right after this move.
    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    // What a game review made of this move: "?!", "?" or "??", in the colour
    // the severity deserves. Empty for a move with nothing to say about it,
    // which is most of them - marking every good move would drown the few
    // that matter.
    public string Annotation
    {
        get => _annotation;
        private set => SetProperty(ref _annotation, value);
    }

    public Brush AnnotationBrush
    {
        get => _annotationBrush;
        private set => SetProperty(ref _annotationBrush, value);
    }

    public void SetQuality(MoveQuality quality)
    {
        (Annotation, AnnotationBrush) = quality switch
        {
            MoveQuality.Blunder => ("??", Blunder),
            MoveQuality.Mistake => ("?", Mistake),
            MoveQuality.Inaccuracy => ("?!", Inaccuracy),
            _ => ("", Brushes.Transparent),
        };
    }

    private static readonly Brush Blunder = Frozen("#E05A45");
    private static readonly Brush Mistake = Frozen("#E08B2E");
    private static readonly Brush Inaccuracy = Frozen("#D8C24A");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public MoveCellViewModel(PlayedMove played, int ply)
    {
        Ply = ply;
        string san = played.San;

        // Castling has no figurine: "O-O" is already a symbol.
        if (san.StartsWith('O'))
        {
            Text = san;
            return;
        }

        int start = 0;
        if (san.Length > 0 && "NBRQK".IndexOf(san[0]) >= 0)
        {
            LeadFigurine = PieceImageProvider.Get(Color.White, PieceFromLetter(san[0]));
            start = 1;
        }

        int equals = san.IndexOf('=');
        if (equals < 0)
        {
            Text = san[start..];
            return;
        }

        // "exd8=Q#": text up to the '=', the promoted piece as a figurine, and
        // whatever check mark trails it.
        Text = san[start..equals];
        if (equals + 1 < san.Length)
        {
            TrailFigurine = PieceImageProvider.Get(Color.White, PieceFromLetter(san[equals + 1]));
            Suffix = san[(equals + 2)..];
        }
    }

    private static PieceType PieceFromLetter(char c) => c switch
    {
        'N' => PieceType.Knight,
        'B' => PieceType.Bishop,
        'R' => PieceType.Rook,
        'Q' => PieceType.Queen,
        'K' => PieceType.King,
        _ => PieceType.Pawn,
    };
}

// One row of the move list: the move number and the pair of moves that share
// it. The black cell is null on the last row of a game that ended on white's
// move, and the white cell is null when the game started from a black move.
public sealed class MoveRowViewModel(int number, MoveCellViewModel? white, MoveCellViewModel? black)
{
    public int Number { get; } = number;
    public string NumberText { get; } = $"{number}.";
    public MoveCellViewModel? White { get; } = white;
    public MoveCellViewModel? Black { get; set; } = black;
}
