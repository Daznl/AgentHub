using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace AgentHub.Controls;

/// <summary>
/// Keeps a window inside the work area of the screen it opens on. Each window's XAML size suits a large monitor; on a
/// laptop at 150% scaling (1280 × 720 logical pixels) an 840-pixel-tall window centred on its owner starts above the
/// screen, with its title bar and header, close buttons included, out of reach. Applied once the window has its handle,
/// before it is shown: too large shrinks to the work area, and whatever hangs over an edge is moved back in.
/// </summary>
public static class WindowPlacement
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    /// <summary>Applies once the window has its handle; safe to call from a constructor.</summary>
    public static void KeepOnScreen(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (window.WindowState != WindowState.Normal) return;
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var dpi = VisualTreeHelper.GetDpi(window);
            var work = new Rect(screen.WorkingArea.Left / dpi.DpiScaleX, screen.WorkingArea.Top / dpi.DpiScaleY,
                screen.WorkingArea.Width / dpi.DpiScaleX, screen.WorkingArea.Height / dpi.DpiScaleY);
            var current = new Rect(rect.Left / dpi.DpiScaleX, rect.Top / dpi.DpiScaleY,
                (rect.Right - rect.Left) / dpi.DpiScaleX, (rect.Bottom - rect.Top) / dpi.DpiScaleY);
            var fitted = Fit(current, work);
            if (fitted == current) return;
            window.Left = fitted.Left;
            window.Top = fitted.Top;
            window.Width = fitted.Width;
            window.Height = fitted.Height;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // No screen or DPI to go by: the window stays where WPF put it.
        }
    }

    /// <summary>The window rectangle shrunk to the work area and moved inside it; unchanged when it already fits.</summary>
    public static Rect Fit(Rect window, Rect work)
    {
        var width = Math.Min(window.Width, work.Width);
        var height = Math.Min(window.Height, work.Height);
        var left = Math.Max(work.Left, Math.Min(window.Left, work.Right - width));
        var top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - height));
        return new Rect(left, top, width, height);
    }
}
