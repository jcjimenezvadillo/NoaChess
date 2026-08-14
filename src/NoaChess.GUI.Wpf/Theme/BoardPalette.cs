using System.Windows.Media;

namespace NoaChess.GUI.Wpf.Theme;

// Colour scheme of the board itself: the two square colours and the colour the
// coordinate labels take on each of them.
//
// The brushes are frozen. All 64 squares share the same two instances, and a
// frozen brush skips WPF's change tracking and can be handed straight to the
// render thread.
public sealed class BoardPalette
{
    public required string Name { get; init; }
    public required SolidColorBrush Light { get; init; }
    public required SolidColorBrush Dark { get; init; }

    // Labels are drawn in the colour of the OPPOSITE square, the convention
    // every major board uses: it reads at any size without a halo.
    public SolidColorBrush LightLabel => Dark;
    public SolidColorBrush DarkLabel => Light;

    public override string ToString() => Name;

    public static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public static IReadOnlyList<BoardPalette> All { get; } =
    [
        new BoardPalette { Name = "Noa",    Light = Frozen("#F0D9B5"), Dark = Frozen("#B58863") },
        new BoardPalette { Name = "Forest", Light = Frozen("#EDEED1"), Dark = Frozen("#779556") },
        new BoardPalette { Name = "Slate",  Light = Frozen("#DEE3E6"), Dark = Frozen("#8CA2AD") },
        new BoardPalette { Name = "Walnut", Light = Frozen("#D9C4A3"), Dark = Frozen("#8B5E3C") },
        new BoardPalette { Name = "Ink",    Light = Frozen("#C6CBD4"), Dark = Frozen("#5A6478") },
    ];

    public static BoardPalette ByName(string? name) =>
        All.FirstOrDefault(p => p.Name == name) ?? All[0];
}
