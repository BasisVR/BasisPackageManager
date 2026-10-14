using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackageListsButtonTests
{
    private const string OneList = """[{ "id": "basis-recommended", "name": "Basis Recommended", "packages": [{ "id": "com.example.toon", "gitUrl": "https://github.com/example/toon.git" }] }]""";

    [AvaloniaFact]
    public void The_package_lists_button_shows_only_while_the_registry_has_lists()
    {
        Localizer.Instance.SetLanguage("en");
        using var registry = new LocalRegistry(OneList);
        var vm = new MainWindowViewModel().PackagesVM;

        _ = vm.LoadCatalogAsync(registry.CatalogUrl);
        Pump(() => vm.HasRegistryPackageLists);
        Assert.True(vm.HasRegistryPackageLists);

        registry.PackageLists = "[]";
        vm.RefreshCommand.Execute(null);
        Pump(() => !vm.HasRegistryPackageLists && vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.HasRegistryPackageLists);
        Assert.True(vm.RefreshCommand.CanExecute(null));

        registry.PackageLists = OneList;
        vm.RefreshCommand.Execute(null);
        Pump(() => vm.HasRegistryPackageLists && vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.HasRegistryPackageLists);
    }

    private static void Pump(Func<bool> done)
    {
        for (var i = 0; i < 1000 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private sealed class LocalRegistry : IDisposable
    {
        private readonly HttpListener _listener = new();

        public LocalRegistry(string packageLists)
        {
            PackageLists = packageLists;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            CatalogUrl = $"http://127.0.0.1:{port}/catalog.json";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = ServeAsync();
        }

        public string CatalogUrl { get; }
        public volatile string PackageLists;

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { return; }
                _ = RespondAsync(context);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath;
            if (path == "/catalog.json") await Task.Delay(300);
            var body = path switch
            {
                "/catalog.json" => """{ "name": "Test", "packages": {} }""",
                "/packagelists.json" => PackageLists,
                _ => null,
            };
            try
            {
                context.Response.StatusCode = body is null ? 404 : 200;
                if (body is not null)
                {
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or IOException) { }
        }

        public void Dispose() => _listener.Close();
    }
}
