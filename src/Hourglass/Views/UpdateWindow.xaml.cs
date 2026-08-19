using System.ComponentModel;
using System.Windows;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class UpdateWindow : Window
{
    private readonly UpdateViewModel _viewModel;
    private bool _isClosing;

    public UpdateWindow(UpdateViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
    }

    /// <summary>True when the new build is staged and the app must restart.</summary>
    public bool ShouldRestart { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _viewModel.Completed -= OnCompleted;
        base.OnClosing(e);
    }

    private void OnCompleted(object? sender, bool shouldRestart)
    {
        ShouldRestart = shouldRestart;

        if (_isClosing)
            return;

        DialogResult = shouldRestart;
    }
}
