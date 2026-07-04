using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BasisPM.App.Services;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;

namespace BasisPM.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainWindowViewModel();
            var window = new MainWindow { DataContext = vm };
            desktop.MainWindow = window;

            // A clean exit clears the crash marker; a crash or force-close leaves it for the next launch.
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
        catch { }
    }
}
