using System.ComponentModel;
using System.Windows;
using Hourglass.Services;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SystemTrayService _tray;
    private bool _isExiting;

    public MainWindow(MainViewModel viewModel, SystemTrayService tray)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _tray = tray;
        DataContext = viewModel;

        _tray.ShowRequested += OnShowRequested;
        _tray.ExitRequested += OnExitRequested;

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
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
