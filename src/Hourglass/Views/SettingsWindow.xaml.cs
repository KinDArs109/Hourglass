using System.ComponentModel;
using System.Windows;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Detach();
        base.OnClosing(e);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
