using System.Windows;
using Hourglass.Utilities;
using Hourglass.ViewModels;

namespace Hourglass.Views;

public partial class AchievementsWindow : Window
{
    public AchievementsWindow(AchievementsViewModel viewModel)
    {
        InitializeComponent();

        DataContext = viewModel;
        Title = $"Достижения — {viewModel.AccountName}";

        SourceInitialized += (_, _) => DwmHelper.ApplyDarkTitleBar(this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
