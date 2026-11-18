using System.Globalization;
using System.Windows;
using System.Windows.Media;
using AgentHub.Controls;
using AgentHub.Models;
using Part = AgentHub.Controls.BadgeToneBrushConverter.TonePart;

namespace AgentHub.Tests;

/// <summary>
/// The repository badges kept their colours when the models stopped carrying brushes: each tone draws exactly the
/// colours the models used to hold.
/// </summary>
public class BadgeToneBrushConverterTests
{
    [Theory]
    [InlineData(BadgeTone.UpToDate, "#065F46", "#6EE7B7")]
    [InlineData(BadgeTone.Behind, "#92400E", "#FEE68A")]
    [InlineData(BadgeTone.Ahead, "#1E40AF", "#93C5FD")]
    [InlineData(BadgeTone.Dirty, "#854D0E", "#FEF08A")]
    [InlineData(BadgeTone.NotCloned, "#27272A", "#A1A1AA")]
    [InlineData(BadgeTone.Missing, "#991B1B", "#FECACA")]
    [InlineData(BadgeTone.Private, "#312E81", "#E0E7FF")]
    [InlineData(BadgeTone.Public, "#064E3B", "#A7F3D0")]
    [InlineData(BadgeTone.AddedButton, "#1E293B", "#FFFFFF")]
    [InlineData(BadgeTone.CloneButton, "#2563EB", "#FFFFFF")]
    public void Each_tone_draws_its_old_colours(BadgeTone tone, string background, string foreground)
    {
        Assert.Equal(background, Hex(BadgeToneBrushConverter.BrushFor(tone, Part.Background)));
        Assert.Equal(foreground, Hex(BadgeToneBrushConverter.BrushFor(tone, Part.Foreground)));
    }

    [Fact]
    public void The_converter_draws_the_part_it_is_set_to_and_ignores_anything_else()
    {
        var background = new BadgeToneBrushConverter { Part = Part.Background };
        var foreground = new BadgeToneBrushConverter { Part = Part.Foreground };
        Assert.Equal("#991B1B", Hex((SolidColorBrush)background.Convert(BadgeTone.Missing, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Equal("#FECACA", Hex((SolidColorBrush)foreground.Convert(BadgeTone.Missing, typeof(Brush), null!, CultureInfo.InvariantCulture)));
        Assert.Same(DependencyProperty.UnsetValue, background.Convert("not a tone", typeof(Brush), null!, CultureInfo.InvariantCulture));
    }

    private static string Hex(SolidColorBrush brush) => $"#{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}";
}
