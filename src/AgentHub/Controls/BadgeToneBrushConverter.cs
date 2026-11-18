using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AgentHub.Models;
using Color = System.Windows.Media.Color;

namespace AgentHub.Controls;

/// <summary>Draws a <see cref="BadgeTone"/>: its background or its text colour, as <see cref="Part"/> says.</summary>
public sealed class BadgeToneBrushConverter : IValueConverter
{
    public enum TonePart { Background, Foreground }

    public TonePart Part { get; set; }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Dictionary<BadgeTone, (SolidColorBrush Background, SolidColorBrush Foreground)> Brushes = new()
    {
        [BadgeTone.UpToDate] = (Frozen(6, 95, 70), Frozen(110, 231, 183)),
        [BadgeTone.Behind] = (Frozen(146, 64, 14), Frozen(254, 230, 138)),
        [BadgeTone.Ahead] = (Frozen(30, 64, 175), Frozen(147, 197, 253)),
        [BadgeTone.Dirty] = (Frozen(133, 77, 14), Frozen(254, 240, 138)),
        [BadgeTone.NotCloned] = (Frozen(39, 39, 42), Frozen(161, 161, 170)),
        [BadgeTone.Missing] = (Frozen(153, 27, 27), Frozen(254, 202, 202)),
        [BadgeTone.Private] = (Frozen(49, 46, 129), Frozen(224, 231, 255)),
        [BadgeTone.Public] = (Frozen(6, 78, 59), Frozen(167, 243, 208)),
        [BadgeTone.AddedButton] = (Frozen(30, 41, 59), Frozen(255, 255, 255)),
        [BadgeTone.CloneButton] = (Frozen(37, 99, 235), Frozen(255, 255, 255)),
    };

    public static SolidColorBrush BrushFor(BadgeTone tone, TonePart part) =>
        part == TonePart.Background ? Brushes[tone].Background : Brushes[tone].Foreground;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is BadgeTone tone ? BrushFor(tone, Part) : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
