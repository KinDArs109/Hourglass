using System.Windows;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class ProxyWindow : Window
{
    public ProxyWindow(AccountViewModel account)
    {
        InitializeComponent();

        DataContext = account;
        Title = $"Прокси — {account.DisplayName}";

        SourceInitialized += (_, _) =>
        {
            DwmHelper.ApplyDarkTitleBar(this);
            DwmHelper.RemoveMinimizeAndMaximize(this);
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
