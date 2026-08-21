using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Hourglass.Services;
using Hourglass.Services.Interfaces;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class MainWindow : Window
{
    /// <summary>Windows asking whether the session may end.</summary>
    private const int QueryEndSession = 0x0011;

    /// <summary>
    /// Set when the request comes from an installer clearing the way for its own update,
    /// rather than from Windows actually shutting down.
    /// </summary>
    private const long EndSessionCloseApp = 0x00000001;

    private readonly MainViewModel _viewModel;
    private readonly SystemTrayService _tray;
    private readonly IAppLogger _logger;
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel, SystemTrayService tray, IAppLogger logger)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _tray = tray;
        _logger = logger;
        DataContext = viewModel;

        _tray.ShowRequested += OnShowRequested;
        _tray.ExitRequested += OnExitRequested;
        _viewModel.RestartRequested += OnExitRequested;

        SourceInitialized += (_, _) =>
        {
            DwmHelper.ApplyDarkTitleBar(this);

            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(OnWindowMessage);
        };
    }

    /// <summary>
    /// Any installer on the machine can ask every window to close so it can replace its
    /// own files, and Windows delivers that as the same message it uses for a shutdown.
    /// Obeying it means the boost dies whenever some unrelated program updates itself,
    /// which is how a night of idling turns into nothing. That request is declined; a
    /// real shutdown or sign-out is left to take its normal course.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != QueryEndSession || (lParam.ToInt64() & EndSessionCloseApp) == 0)
            return IntPtr.Zero;

        _logger.Info(AppLogScopes.App,
            "Установщик другой программы просил закрыться — отказались, накрутка продолжается");

        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Brings the window back from the tray.</summary>
    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray)
            Hide();

        base.OnStateChanged(e);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_isExiting)
        {
            _tray.ShowRequested -= OnShowRequested;
            _tray.ExitRequested -= OnExitRequested;
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;

        if (_viewModel.MinimizeToTray)
        {
            Hide();
            _tray.Notify("Hourglass свернулся в трей",
                "Накрутка продолжается. Значок в области уведомлений — правый клик для меню.");
            return;
        }

        BeginExit();
    }

    private void OnShowRequested(object? sender, EventArgs e) => RestoreFromTray();

    private void OnExitRequested(object? sender, EventArgs e) => BeginExit();

    private async void BeginExit()
    {
        if (_isExiting)
            return;

        _isExiting = true;
        Hide();

        // Clear the played-games state on Steam's side before the process goes away.
        await _viewModel.ShutdownAsync();

        Application.Current.Shutdown();
    }
}
