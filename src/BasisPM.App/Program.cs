using Avalonia;
using BasisPM.App.Services;
using BasisPM.Core.Services;
using Velopack;

namespace BasisPM.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        InvariantGlobalization.EnableIfIcuMissing();
        if (OperatingSystem.IsLinux() && CommandLineTool.IsCliInvocation(Environment.GetEnvironmentVariable("ARGV0"), Environment.GetEnvironmentVariable("APPIMAGE")) && CommandLineTool.RunBundled(args) is { } exitCode) return exitCode;
        // Must run first: services Velopack's install/update/uninstall hooks. No-op from source.
        VelopackApp.Build().Run();

        try { Environment.CurrentDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); }
        catch (Exception ex) { DiagnosticLog.Write("Moving the working directory out of the install folder", ex); }
        ToolSearchPath.Apply();

        var uri = args.FirstOrDefault(DeepLink.IsDeepLink);

        // Single instance: a second launch forwards its deep link to the running window and exits.
        try
        {
            if (!SingleInstance.TryBecomePrimary())
            {
                SingleInstance.ForwardToPrimary(uri ?? DeepLink.ActivateUri);
                return 0;
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
            if (packaged) DesktopIntegration.RefreshApplicationsMenu();
        }
        catch (Exception ex) { DiagnosticLog.Write("Registering the basispm protocol handler", ex); }

        DeepLinkDispatcher.Deliver(uri);
        try { SingleInstance.StartServer(DeepLinkDispatcher.Deliver); } catch (Exception ex) { DiagnosticLog.Write("Starting the deep-link listener", ex); }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
