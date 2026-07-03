using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && DataContext is MainWindowViewModel vm)
            vm.NavigateTo(tag);
    }

    private async void OnCopyStatusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var clipboard = Clipboard;
        if (clipboard is null || string.IsNullOrEmpty(vm.StatusMessage)) return;
        try
        {
            await clipboard.SetTextAsync(vm.StatusMessage);
            vm.SetStatus("Copied status to clipboard.", StatusKind.Success);
        }
        catch (Exception ex)
        {
            vm.SetStatus($"Copy failed: {ex.Message}", StatusKind.Error);
        }
    }

    private void OnDismissStatusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DismissStatus();
    }
}
