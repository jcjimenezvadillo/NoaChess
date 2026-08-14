using System.Windows;
using System.Windows.Media;

namespace NoaChess.GUI.Wpf.ViewModels;

// An arrow drawn over the board, showing one of the moves the engine has
// priced.
//
// The geometry is built in BOARD coordinates, because an arrow belongs to two
// squares of the board as it is currently oriented. Flipping the board rebuilds
// them all rather than transforming anything at render time.
public sealed class BoardArrow
{
    public required Geometry Shape { get; init; }
    public required Brush Fill { get; init; }

    // Builds an arrow between two DISPLAY indices (0 = top-left of the board as
    // it is currently oriented) on a board whose squares are 'size' units.
    // 'weight' scales the thickness, so the move the engine actually prefers is
    // drawn heavier than the alternatives.
    public static BoardArrow Build(int fromDisplay, int toDisplay, double size, Brush fill,
                                   double weight = 1.0)
    {
        Point a = Center(fromDisplay, size);
        Point b = Center(toDisplay, size);

        var direction = new Vector(b.X - a.X, b.Y - a.Y);
        double length = direction.Length;
        if (length < 0.001)
            direction = new Vector(0, -1);
        else
            direction /= length;
        var normal = new Vector(-direction.Y, direction.X);

        // The tail starts clear of the piece it leaves and the head stops short
        // of the centre of the target, so both squares stay readable.
        double shaftHalf = size * 0.075 * weight;
        double headHalf = size * 0.19 * weight;
        double headLength = size * 0.27 * weight;

        Point tail = a + direction * (size * 0.30);
        Point tip = b - direction * (size * 0.10);
        Point neck = tip - direction * headLength;

        var figure = new PathFigure { StartPoint = tail + normal * shaftHalf, IsClosed = true };
        figure.Segments.Add(new LineSegment(neck + normal * shaftHalf, true));
        figure.Segments.Add(new LineSegment(neck + normal * headHalf, true));
        figure.Segments.Add(new LineSegment(tip, true));
        figure.Segments.Add(new LineSegment(neck - normal * headHalf, true));
        figure.Segments.Add(new LineSegment(neck - normal * shaftHalf, true));
        figure.Segments.Add(new LineSegment(tail - normal * shaftHalf, true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        geometry.Freeze();

        return new BoardArrow { Shape = geometry, Fill = fill };
    }

    private static Point Center(int displayIndex, double size) =>
        new((displayIndex % 8 + 0.5) * size, (displayIndex / 8 + 0.5) * size);

    // Colour of a move that is 'loss' centipawns worse than the best one, on a
    // green to yellow to orange scale.
    //
    // The scale saturates at three pawns: past that every move is simply bad,
    // and stretching the ramp further would only make good moves look worse
    // than they are. The best move is also the most opaque, so the eye finds it
    // before it reads any of them.
    public static Brush ColourForLoss(int centipawnLoss)
    {
        double t = Math.Clamp(centipawnLoss / 300.0, 0, 1);

        (byte R, byte G, byte B) green = (0x5C, 0xB8, 0x4A);
        (byte R, byte G, byte B) yellow = (0xD8, 0xC2, 0x3A);
        (byte R, byte G, byte B) orange = (0xE0, 0x7B, 0x28);

        (byte r, byte g, byte b) = t < 0.5
            ? Mix(green, yellow, t * 2)
            : Mix(yellow, orange, (t - 0.5) * 2);

        byte alpha = (byte)(0xC0 - 0x50 * t);
        var brush = new SolidColorBrush(Color.FromArgb(alpha, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static (byte, byte, byte) Mix((byte R, byte G, byte B) from,
                                          (byte R, byte G, byte B) to, double t)
        => ((byte)(from.R + (to.R - from.R) * t),
            (byte)(from.G + (to.G - from.G) * t),
            (byte)(from.B + (to.B - from.B) * t));
}
