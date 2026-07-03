using Avalonia;
using BasisPM.App.Services;
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
        catch { /* any failure → continue as a normal launch */ }

        try
        {
            var packaged = false;
            try { packaged = new UpdateService().IsSupported; } catch { }
            DeepLink.RegisterProtocolIfPackaged(packaged);
        }
        catch { }

        DeepLinkDispatcher.Pending = uri;
        try { SingleInstance.StartServer(DeepLinkDispatcher.Raise); } catch { }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
