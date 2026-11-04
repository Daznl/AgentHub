using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentHub.Controls;

/// <summary>
/// Paints a window's native chrome to match the app: dark title bar, caption and frame border in the window background
/// colour instead of Windows' light defaults. Needs Windows 10 1809 for the dark mode and Windows 11 for the colours;
/// on older builds the calls fail quietly and the chrome stays as it was.
/// </summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int UseImmersiveDarkMode = 20, UseImmersiveDarkModeBefore20H1 = 19, BorderColor = 34, CaptionColor = 35;
    // COLORREF is 0x00BBGGRR: the app's window background #0C121C.
    private const int Chrome = 0x001C120C;

    /// <summary>Applies once the window has its handle; safe to call from a constructor.</summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) ApplyNow(window);
        else window.SourceInitialized += (_, _) => ApplyNow(window);
    }

    private static void ApplyNow(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var useDark = 1;
            if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref useDark, sizeof(int));
            var colour = Chrome;
            DwmSetWindowAttribute(hwnd, CaptionColor, ref colour, sizeof(int));
            DwmSetWindowAttribute(hwnd, BorderColor, ref colour, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No Desktop Window Manager here: the chrome stays default.
        }
    }
}
