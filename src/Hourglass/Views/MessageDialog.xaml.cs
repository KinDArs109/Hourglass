using System.Windows;
using Hourglass.Utilities;

namespace Hourglass.Views;

/// <summary>Themed stand-in for MessageBox so dialogs match the rest of the app.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, bool isConfirmation, string confirmText)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Visibility = isConfirmation ? Visibility.Visible : Visibility.Collapsed;

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText = "Да")
    {
        var dialog = new MessageDialog(title, message, isConfirmation: true, confirmText);
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        return dialog.ShowDialog() == true;
    }

    public static void Notice(Window? owner, string title, string message)
    {
        var dialog = new MessageDialog(title, message, isConfirmation: false, "Понятно");
        if (owner is { IsVisible: true })
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
