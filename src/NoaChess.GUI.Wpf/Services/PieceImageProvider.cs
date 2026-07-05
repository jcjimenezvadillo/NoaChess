using System.Windows;
using System.Windows.Media;
using NoaChess.Core;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;
using Color = NoaChess.Core.Color; // Disambiguate from System.Windows.Media.Color.

namespace NoaChess.GUI.Wpf.Services;

// Loads the piece SVG assets (classic "Cburnett" set) and converts them into
// WPF DrawingImage vectors using SharpVectors. Being vectors, the pieces
// rescale in real time with the board without any pixelation.
//
// Each of the 12 images is converted only once and cached: the conversion is
// cheap but the board refreshes 64 squares on every move.
public static class PieceImageProvider
{
    // SVG file names, indexed as [color, pieceType] (p=pawn, n=knight,
    // b=bishop, r=rook, q=queen, k=king; l=light/white, d=dark/black).
    private static readonly string[,] FileNames =
    {
        // White                                (index = PieceType)
        { "pl", "nl", "bl", "rl", "ql", "kl" },
        // Black
        { "pd", "nd", "bd", "rd", "qd", "kd" }
    };

    private static readonly Dictionary<string, DrawingImage> Cache = [];

    // Returns the vector image for the given piece, loading it on first use.
    public static DrawingImage Get(Color color, PieceType type)
    {
        string name = FileNames[(int)color, (int)type];
        if (Cache.TryGetValue(name, out DrawingImage? cached))
            return cached;

        // The SVG is embedded as a WPF resource; read it via its pack URI and
        // let SharpVectors turn it into a WPF drawing tree.
        var uri = new Uri($"pack://application:,,,/Resources/Pieces/{name}.svg");
        using var stream = Application.GetResourceStream(uri)!.Stream;

        var reader = new FileSvgReader(new WpfDrawingSettings());
        DrawingGroup drawing = reader.Read(stream);

        var image = new DrawingImage(drawing);
        image.Freeze(); // Frozen visuals are thread-safe and faster to render.
        Cache[name] = image;
        return image;
    }
}
