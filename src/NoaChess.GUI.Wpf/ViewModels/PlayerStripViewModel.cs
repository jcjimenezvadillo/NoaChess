using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NoaChess.Core;
using NoaChess.GUI.Wpf.Services;
using Color = NoaChess.Core.Color; // Disambiguate from System.Windows.Media.Color.

namespace NoaChess.GUI.Wpf.ViewModels;

// The strip above and below the board: who is playing that side, the pieces
// they have captured and by how much material they are ahead.
public sealed class PlayerStripViewModel : ViewModelBase
{
    private static readonly int[] PieceValues = [1, 3, 3, 5, 9, 0];
    private static readonly int[] InitialCounts = [8, 2, 2, 2, 1, 1];

    private string _name = "";
    private string _role = "";
    private string _materialText = "";
    private bool _isToMove;
    private string _clockText = "";
    private bool _hasClock;
    private bool _isLowTime;

    // Colour this strip belongs to. It changes when the board is flipped,
    // because the strip is a position on screen, not a player.
    public Color Color { get; private set; }

    // The pieces this side is UP BY, drawn as a compact row.
    public ObservableCollection<ImageSource> Captured { get; } = [];

    // One frozen instance shared by every dark piece on screen.
    private static readonly Effect BlackPieceHalo = CreateHalo();

    private static Effect CreateHalo()
    {
        var halo = new DropShadowEffect
        {
            Color = System.Windows.Media.Colors.White,
            ShadowDepth = 0,
            BlurRadius = 5,
            Opacity = 0.75,
        };
        halo.Freeze();
        return halo;
    }

    private Effect? _capturedHalo;

    // Null when the row needs no help, which is the row of white pieces.
    public Effect? CapturedHalo
    {
        get => _capturedHalo;
        private set => SetProperty(ref _capturedHalo, value);
    }

    public string Name
    {
        get => _name;
        private set => SetProperty(ref _name, value);
    }

    public string Role
    {
        get => _role;
        private set => SetProperty(ref _role, value);
    }

    // "+3" when this side is up material, empty when level or behind: only the
    // player who is ahead shows the number, as every board does.
    public string MaterialText
    {
        get => _materialText;
        private set => SetProperty(ref _materialText, value);
    }

    public bool IsToMove
    {
        get => _isToMove;
        private set => SetProperty(ref _isToMove, value);
    }

    public string ClockText
    {
        get => _clockText;
        private set => SetProperty(ref _clockText, value);
    }

    // False in games with no clock, which hides the whole clock face rather
    // than showing a meaningless zero.
    public bool HasClock
    {
        get => _hasClock;
        private set => SetProperty(ref _hasClock, value);
    }

    // Under twenty seconds. The view turns the clock red on it.
    public bool IsLowTime
    {
        get => _isLowTime;
        private set => SetProperty(ref _isLowTime, value);
    }

    // Called on every clock tick, so it does only this and touches nothing else.
    public void SetClock(long remainingMs, bool visible)
    {
        HasClock = visible;
        if (!visible)
            return;
        ClockText = GameClock.Format(remainingMs);
        IsLowTime = remainingMs < 20_000;
    }

    public void Update(Board board, Color color, string name, string role)
    {
        Color = color;
        Name = name;
        Role = role;
        IsToMove = board.SideToMove == color;

        // Only the SURPLUS is shown, not everything taken. A pawn each way is
        // nobody's advantage, and drawing both cancels visually to nothing
        // while filling the whole strip. Per piece type that surplus is simply
        // how many more of it this side still has, because both armies started
        // with the same number: (initial - theirs) - (initial - mine) reduces
        // to mine - theirs. Promotions can push a count above its start value,
        // which the clamp handles.
        Color victim = Board.OppositeColor(color);
        Captured.Clear();
        int advantage = 0;

        for (int type = 0; type < 5; type++)
        {
            var pieceType = (PieceType)type;
            int theirs = Bitboard.PopCount(board.Pieces(victim, pieceType));
            int mine = Bitboard.PopCount(board.Pieces(color, pieceType));

            // Drawn in the OPPONENT's colour: these are their pieces, the ones
            // this side is up by.
            for (int i = 0; i < Math.Max(0, mine - theirs); i++)
                Captured.Add(PieceImageProvider.Get(victim, pieceType));

            advantage += (mine - theirs) * PieceValues[type];
        }

        // Black pieces on a dark panel are a silhouette on a silhouette. A
        // light halo behind them is what makes them readable without touching
        // the piece set; white pieces need nothing and get nothing.
        CapturedHalo = victim == Color.Black ? BlackPieceHalo : null;

        MaterialText = advantage > 0 ? $"+{advantage}" : "";
    }
}
