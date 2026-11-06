using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace AgentHub.Controls;

public enum RichSpanKind { Plain, Bold, Code, LineBreak }

/// <summary>One piece of a helper reply: plain, bold or code text, or a line break.</summary>
public sealed record RichSpan(string Text, RichSpanKind Kind);

/// <summary>
/// The small slice of Markdown the helpers reply in, shown in a TextBlock: <c>**bold**</c>, <c>`code`</c>, lines starting
/// <c>- </c> or <c>* </c> as bullets, and <c>#</c> headings as bold lines. Anything else is shown as typed.
/// Set with <c>controls:RichText.Markup="{Binding Text}"</c>.
/// </summary>
public static partial class RichText
{
    public static readonly DependencyProperty MarkupProperty = DependencyProperty.RegisterAttached(
        "Markup", typeof(string), typeof(RichText), new PropertyMetadata("", OnMarkupChanged));

    public static string GetMarkup(DependencyObject element) => (string)element.GetValue(MarkupProperty);
    public static void SetMarkup(DependencyObject element, string value) => element.SetValue(MarkupProperty, value);

    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");
    private static readonly SolidColorBrush CodeBack = Frozen(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));

    [GeneratedRegex(@"\*\*(.+?)\*\*|`([^`]+)`")]
    private static partial Regex InlinePattern();

    [GeneratedRegex(@"^(\s*)[-*]\s+")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^#{1,6}\s+")]
    private static partial Regex HeadingPattern();

    /// <summary>Splits <paramref name="markup"/> into spans. Pure, so it is unit-tested apart from WPF.</summary>
    public static IReadOnlyList<RichSpan> Parse(string? markup)
    {
        var spans = new List<RichSpan>();
        var lines = (markup ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) spans.Add(new RichSpan("\n", RichSpanKind.LineBreak));
            var line = lines[i];
            if (HeadingPattern().Match(line) is { Success: true } heading)
            {
                spans.Add(new RichSpan(line[heading.Length..].Replace("**", ""), RichSpanKind.Bold));
                continue;
            }
            if (BulletPattern().Match(line) is { Success: true } bullet)
            {
                spans.Add(new RichSpan(new string(' ', Math.Min(bullet.Groups[1].Length, 6)) + "•  ", RichSpanKind.Plain));
                line = line[bullet.Length..];
            }
            var at = 0;
            foreach (Match m in InlinePattern().Matches(line))
            {
                if (m.Index > at) spans.Add(new RichSpan(line[at..m.Index], RichSpanKind.Plain));
                spans.Add(m.Groups[1].Success ? new RichSpan(m.Groups[1].Value, RichSpanKind.Bold) : new RichSpan(m.Groups[2].Value, RichSpanKind.Code));
                at = m.Index + m.Length;
            }
            if (at < line.Length) spans.Add(new RichSpan(line[at..], RichSpanKind.Plain));
        }
        return spans;
    }

    private static void OnMarkupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block) return;
        block.Inlines.Clear();
        foreach (var span in Parse(e.NewValue as string))
        {
            block.Inlines.Add(span.Kind switch
            {
                RichSpanKind.LineBreak => new LineBreak(),
                RichSpanKind.Bold => new Bold(new Run(span.Text)),
                RichSpanKind.Code => new Run(span.Text) { FontFamily = CodeFont, Background = CodeBack },
                _ => new Run(span.Text)
            });
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
