using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Hourglass.Services;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;
    private bool _isClosing;
    private bool _isClosingFromResult;

    public LoginWindow(LoginViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.Completed += OnCompleted;

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Set when the sign-in succeeded; null when the user backed out.</summary>
    public LoginResult? Result { get; private set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;

        if (!_isClosingFromResult)
            _viewModel.Cancel();

        _viewModel.Completed -= OnCompleted;
        PasswordInput.Clear();
        base.OnClosing(e);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsUsernameLocked)
            PasswordInput.Focus();
        else
            UsernameInput.Focus();
    }

    private async void OnSignInClick(object sender, RoutedEventArgs e) =>
        await _viewModel.SubmitAsync(PasswordInput.Password);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if (_viewModel.IsCredentialsStep)
        {
            e.Handled = true;
            OnSignInClick(this, new RoutedEventArgs());
            return;
        }

        if (_viewModel.IsCodePrompt && _viewModel.SubmitGuardCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.SubmitGuardCommand.Execute(null);
        }
    }

    private void OnCompleted(object? sender, LoginResult? result)
    {
        Result = result;

        // Reached from OnClosing's cancel path — the window is already going away.
        if (_isClosing)
            return;

        _isClosingFromResult = true;
        DialogResult = result is not null;
    }
}
