using Avalonia.Controls;
using Avalonia.Interactivity;

namespace BasisPM.App.Views;

public partial class SyncBlockedWindow : Window
{
    public SyncBlockedWindow() => InitializeComponent();

    private void OnKeepMine(object? sender, RoutedEventArgs e) => Close(true);

    private void OnTakeTheirs(object? sender, RoutedEventArgs e) => Close(false);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
