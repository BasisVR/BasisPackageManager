using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LogsViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(vm.AllText());
    }
}
