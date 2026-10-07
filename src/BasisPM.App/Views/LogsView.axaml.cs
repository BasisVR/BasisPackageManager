using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class LogsView : UserControl
{
    public LogsView() => InitializeComponent();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(vm.AllText()); }
        catch (Exception ex) { DiagnosticLog.Write("Copying the activity log to the clipboard", ex); }
    }
}
