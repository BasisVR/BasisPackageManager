using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class NeedsYouWindow : Window
{
    public NeedsYouWindow() => InitializeComponent();

    private void OnFix(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is NeedsYouItem item) Close(item);
    }

    private void OnGetUnity(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is UpdatedItem item) Close(item);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
