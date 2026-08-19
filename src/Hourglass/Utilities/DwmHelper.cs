using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Hourglass.Utilities;

/// <summary>Paints the native window frame dark so the title bar matches the app.</summary>
public static class DwmHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCornerPreference = 33;
    private const int CornerPreferenceRound = 2;

    private const int GwlStyle = -16;
    private const int WsMinimizeBox = 0x00020000;
    private const int WsMaximizeBox = 0x00010000;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public static void ApplyDarkTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var enabled = 1;
        SafeExec.Try(() => DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)));

        // 0x00RRGGBB is stored as COLORREF (0x00BBGGRR).
        var border = 0x00302619;
        SafeExec.Try(() => DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int)));

        var corner = CornerPreferenceRound;
        SafeExec.Try(() => DwmSetWindowAttribute(handle, DwmwaCornerPreference, ref corner, sizeof(int)));
    }

    /// <summary>Leaves only the close button, while keeping the window resizable.</summary>
    public static void RemoveMinimizeAndMaximize(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        SafeExec.Try(() =>
        {
            var style = GetWindowLong(handle, GwlStyle);
            SetWindowLong(handle, GwlStyle, style & ~WsMinimizeBox & ~WsMaximizeBox);
        });
    }
}
