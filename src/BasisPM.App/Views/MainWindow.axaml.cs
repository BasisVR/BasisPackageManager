using Avalonia.Controls;
using Avalonia.Interactivity;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.App.ViewModels;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class MainWindow : Window
{
    private bool _serverStopConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        // The ambient background (gradient pan + blurred blobs) animates forever, forcing a repaint
        // every frame. Pause it whenever the window isn't in the foreground so a backgrounded or
        // minimized app spends ~no CPU on rendering; it resumes the moment the window is focused.
        Activated += OnForegroundChanged;
        Deactivated += OnForegroundChanged;
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) OnForegroundChanged(this, EventArgs.Empty); };
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_serverStopConfirmed || DataContext is not MainWindowViewModel vm || !vm.ServerVM.IsServerRunning) return;
        e.Cancel = true;
        if (!await Dialogs.ConfirmAsync(L.Tr("server.console.closeTitle"), L.Tr("server.console.closeBody", vm.ServerVM.RunningProjectName))) return;
        await vm.ServerVM.StopServerAsync();
        _serverStopConfirmed = true;
        Close();
    }

    private void OnForegroundChanged(object? sender, EventArgs e)
        => BgRoot.Classes.Set("bgLive", IsActive && WindowState != WindowState.Minimized);

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
            vm.SetStatus(L.Tr("shell.status.copied"), StatusKind.Success);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Copying diagnostic logs to the clipboard", ex);
            vm.SetStatus(L.Tr("shell.status.copyFailed", ex.Message), StatusKind.Error);
        }
    }

    private void OnDismissStatusClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DismissStatus();
    }
}
