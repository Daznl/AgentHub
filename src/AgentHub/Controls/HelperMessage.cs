using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AgentHub;

/// <summary>
/// One line of a helper conversation (the Context window's and the main window's): the user's words (right) or the crab's
/// reply (left). Shown by the <c>HelperMessageTemplate</c> resource.
/// </summary>
public sealed class HelperMessage : INotifyPropertyChanged
{
    private static readonly SolidColorBrush UserBack = Freeze("#12324A"), UserBorder = Freeze("#24506B"), UserFore = Freeze("#E8F1FB");
    private static readonly SolidColorBrush CrabBack = Freeze("#1E140E"), CrabBorder = Freeze("#4A2A18"), CrabFore = Freeze("#EADFD6");
    private static readonly SolidColorBrush ErrorBack = Freeze("#2A1218"), ErrorBorder = Freeze("#5B2230"), ErrorFore = Freeze("#FCA5A5");

    private string _text = "";
    private bool _isError;

    private HelperMessage(bool fromUser) => FromUser = fromUser;

    public static HelperMessage User(string text) => new(true) { Text = text };
    public static HelperMessage Crab(string text) => new(false) { Text = text };

    public bool FromUser { get; }

    public string Text
    {
        get => _text;
        set { _text = value; Changed(nameof(Text)); }
    }

    public bool IsError
    {
        get => _isError;
        set { _isError = value; Changed(nameof(Background)); Changed(nameof(Border)); Changed(nameof(Foreground)); }
    }

    public HorizontalAlignment Alignment => FromUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public Thickness Indent => FromUser ? new Thickness(36, 0, 0, 10) : new Thickness(0, 0, 0, 10);
    public Visibility CrabVisibility => FromUser ? Visibility.Collapsed : Visibility.Visible;
    public SolidColorBrush Background => FromUser ? UserBack : IsError ? ErrorBack : CrabBack;
    public SolidColorBrush Border => FromUser ? UserBorder : IsError ? ErrorBorder : CrabBorder;
    public SolidColorBrush Foreground => FromUser ? UserFore : IsError ? ErrorFore : CrabFore;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
