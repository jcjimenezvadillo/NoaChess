using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NoaChess.GUI.Wpf.Theme;

// Hides an element whose content is missing: the figurine slots of the move
// list and the piece image of an empty square.
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// Ticks the menu entry whose parameter matches the current setting, which is
// how the think time, thread count and board theme menus show their state.
public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
            return false;

        // The parameter arrives from XAML as a string, so both sides are
        // compared in their text form rather than by type.
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

// Colours an evaluation by who it favours. The neutral band matters: painting
// a level position green or red would suggest a decision the engine has not
// made.
public sealed class ScoreToBrushConverter : IValueConverter
{
    private const int NeutralBand = 40; // centipawns

    public Brush Positive { get; set; } = Brushes.YellowGreen;
    public Brush Negative { get; set; } = Brushes.IndianRed;
    public Brush Neutral { get; set; } = Brushes.Gainsboro;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int score)
            return Neutral;
        if (score > NeutralBand)
            return Positive;
        return score < -NeutralBand ? Negative : Neutral;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// True when the value is false. Needed wherever a control has to be enabled by
// the ABSENCE of a state, such as the board while the engine is not thinking.
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

// Turns a 0..1 fraction into a star GridLength, which is how a bar is drawn
// proportionally without the ViewModel needing to know the panel's width.
public sealed class FractionToStarConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        return new System.Windows.GridLength(Invert ? 1 - fraction : fraction,
                                             System.Windows.GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
