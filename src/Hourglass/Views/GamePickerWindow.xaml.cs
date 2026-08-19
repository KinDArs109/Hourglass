using System.ComponentModel;
using System.Windows;
using Hourglass.Models;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class GamePickerWindow : Window
{
    private readonly GamePickerViewModel _viewModel;
    private bool _isClosing;

    public GamePickerWindow(GamePickerViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;

        SourceInitialized += (_, _) =>
        {
            DwmHelper.ApplyDarkTitleBar(this);
            DwmHelper.RemoveMinimizeAndMaximize(this);
        };
    }

    /// <summary>Games the user picked, or null when the dialog was dismissed.</summary>
    public IReadOnlyList<GameConfig>? Result { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        _viewModel.Completed -= OnCompleted;
        base.OnClosing(e);
    }

    private void OnCompleted(object? sender, IReadOnlyList<GameConfig>? selection)
    {
        Result = selection;

        if (_isClosing)
            return;

        DialogResult = selection is not null;
    }
}
