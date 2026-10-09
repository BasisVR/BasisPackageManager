using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Services;

namespace BasisPM.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Both operations have explicit fallback behavior: malformed/missing settings return
            // defaults, and an unknown language falls back to English. Do not mask startup defects.
            var settingsService = new UserSettingsService();
            var startupSettings = settingsService.Load();
            Localizer.Instance.SetLanguage(startupSettings.Language);
            SectionState.Load(settingsService, startupSettings);

            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            WindowSizeState.Attach(window, settingsService, startupSettings);
            desktop.MainWindow = window;

            // Clear as soon as the main window really closes. Waiting only for desktop.Exit is too
            // late when shutdown is delayed by another window or a framework/background teardown.
            // A force-close cannot raise Closed, so it still leaves the marker behind as intended.
            window.Closed += (_, _) => CrashReporter.MarkCleanExit();

            // Keep the lifetime event as a fallback for explicit shutdowns that do not close the
            // main window first (for example an updater-triggered application shutdown).
            desktop.Exit += (_, _) => CrashReporter.MarkCleanExit();

            // basispm:// links forwarded from a second launch while this instance is already running.
            DeepLinkDispatcher.UriReceived += uri => Dispatcher.UIThread.Post(() =>
            {
                Bring(window);
                vm.HandleDeepLink(uri);
            });

            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Bring(Window window)
    {
        try
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();
        }
        catch (Exception ex) { DiagnosticLog.Write("Restoring and activating the main window", ex); }
    }
}
