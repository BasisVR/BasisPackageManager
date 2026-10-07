using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private async void OnCopyLogs(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try { await clipboard.SetTextAsync(vm.Logs.AllText()); }
        catch (Exception ex) { DiagnosticLog.Write("Copying the activity log to the clipboard", ex); }
    }
}
