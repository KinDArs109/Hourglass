using System.Drawing;
using Hourglass.Utilities;
using WinForms = System.Windows.Forms;

namespace Hourglass.Services;

public enum TrayStatus
{
    Idle,
    Active,
    Waiting,
    Attention
}

/// <summary>Owns the notification-area icon and its menu.</summary>
public sealed class SystemTrayService : IDisposable
{
    private static readonly Color IdleColor = Color.FromArgb(0xFF, 0x64, 0x74, 0x8B);
    private static readonly Color ActiveColor = Color.FromArgb(0xFF, 0x43, 0xC4, 0x63);
    private static readonly Color WaitingColor = Color.FromArgb(0xFF, 0xE3, 0xB3, 0x41);
    private static readonly Color AttentionColor = Color.FromArgb(0xFF, 0xF1, 0x59, 0x5B);

    private readonly WinForms.NotifyIcon _notifyIcon;
    private bool _disposed;

    public SystemTrayService()
    {
        _notifyIcon = new WinForms.NotifyIcon
        {
            Text = AppPaths.ProductName,
            Icon = TrayIconFactory.Get(IdleColor),
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;
    public event EventHandler? StartAllRequested;
    public event EventHandler? StopAllRequested;
    public event EventHandler? ExitRequested;

    public void UpdateStatus(TrayStatus status, string tooltip)
    {
        if (_disposed)
            return;

        _notifyIcon.Icon = TrayIconFactory.Get(status switch
        {
            TrayStatus.Active => ActiveColor,
            TrayStatus.Waiting => WaitingColor,
            TrayStatus.Attention => AttentionColor,
            _ => IdleColor
        });

        // NotifyIcon truncates anything past 63 characters.
        _notifyIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..60] + "…";
    }

    public void Notify(string title, string message, bool isWarning = false)
    {
        if (_disposed)
            return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = isWarning ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(5000);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        TrayIconFactory.ClearCache();
    }

    private WinForms.ContextMenuStrip BuildMenu()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            RenderMode = WinForms.ToolStripRenderMode.System
        };

        menu.Items.Add("Открыть Hourglass", null, (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Запустить все", null, (_, _) => StartAllRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Остановить все", null, (_, _) => StopAllRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        return menu;
    }
}
