using Avalonia;
using BasisPM.App.Services;
using BasisPM.Core.Services;
using Velopack;

namespace BasisPM.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: services Velopack's install/update/uninstall hooks. No-op from source.
        VelopackApp.Build().Run();

        var uri = args.FirstOrDefault(DeepLink.IsDeepLink);

        // Single instance: a second launch forwards its deep link to the running window and exits.
        try
        {
            if (!SingleInstance.TryBecomePrimary())
            {
                if (uri is not null) SingleInstance.ForwardToPrimary(uri);
                return;
            }
        }
        catch (Exception ex) { DiagnosticLog.Write("Establishing the primary application instance", ex); /* continue as a normal launch */ }

        // Primary instance only: record unhandled exceptions / unclean shutdowns for the next launch.
        CrashReporter.Install();

        try
        {
            var packaged = false;
            try { packaged = new UpdateService().IsSupported; } catch (Exception ex) { DiagnosticLog.Write("Detecting installed update support", ex); }
            DeepLink.RegisterProtocolIfPackaged(packaged);
        }
        catch (Exception ex) { DiagnosticLog.Write("Registering the basispm protocol handler", ex); }

        DeepLinkDispatcher.Pending = uri;
        try { SingleInstance.StartServer(DeepLinkDispatcher.Raise); } catch (Exception ex) { DiagnosticLog.Write("Starting the deep-link listener", ex); }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Running the application", ex);
            CrashReporter.Write(ex, "Main");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
