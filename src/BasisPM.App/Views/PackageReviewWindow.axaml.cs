using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class PackageReviewWindow : Window
{
    public PackageReviewWindow() => InitializeComponent();

    private void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PackageReviewViewModel { CanInstall: true }) Close(true);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => (DataContext as PackageReviewViewModel)?.OpenFolder();
}
