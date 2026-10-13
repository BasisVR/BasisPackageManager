using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Input;
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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ExpanderToolTip.Register();
    }

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
            if (OperatingSystem.IsMacOS()) SetUpMac(vm, window);

            _ = vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetUpMac(MainWindowViewModel vm, Window window)
    {
        var menu = new NativeMenu();
        menu.Add(MenuItem("shell.macMenu.about", null, vm, window));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(MenuItem("shell.macMenu.settings", new KeyGesture(Key.OemComma, KeyModifiers.Meta), vm, window));
        NativeMenu.SetMenu(this, menu);
        if (TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
            activatable.Activated += (_, e) =>
            {
                if (e is ProtocolActivatedEventArgs { Uri: { } uri } && DeepLink.IsDeepLink(uri.OriginalString)) DeepLinkDispatcher.Deliver(uri.OriginalString);
            };
    }

    private static NativeMenuItem MenuItem(string key, KeyGesture? gesture, MainWindowViewModel vm, Window window)
    {
        var item = new NativeMenuItem { Gesture = gesture };
        item.Bind(NativeMenuItem.HeaderProperty, (IBinding)new TrExtension(key).ProvideValue(null!));
        item.Click += (_, _) =>
        {
            Bring(window);
            vm.NavigateTo("settings");
        };
        return item;
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
