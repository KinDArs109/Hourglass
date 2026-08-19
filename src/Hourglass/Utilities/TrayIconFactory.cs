using System.Drawing;
using System.Runtime.InteropServices;

namespace Hourglass.Utilities;

/// <summary>
/// Builds tray icons from the shared hourglass glyph at the size Windows actually
/// asks for, with a status dot in the corner. Icons are cached per status colour
/// because recreating them churns GDI handles.
/// </summary>
public static class TrayIconFactory
{
    private static readonly Dictionary<int, Icon> Cache = new();
    private static readonly object Gate = new();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Get(Color statusColor)
    {
        var key = statusColor.ToArgb();
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;

            var icon = Create(statusColor);
            Cache[key] = icon;
            return icon;
        }
    }

    public static void ClearCache()
    {
        lock (Gate)
        {
            foreach (var icon in Cache.Values)
                icon.Dispose();
            Cache.Clear();
        }
    }

    private static Icon Create(Color statusColor)
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (size < 16)
            size = 16;

        using var bitmap = HourglassGlyph.Render(size, statusColor);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
