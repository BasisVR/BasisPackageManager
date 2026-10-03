using System.Xml.Linq;
using System.Net;
using System.Net.Sockets;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisServerServiceTests
{
    [Fact]
    public void Runtime_uses_the_servers_normal_release_build_directory()
    {
        using var t = new TempDir();
        var root = t.CreateDir("Basis");
        var paths = new BasisServerService().GetPaths(root);

        Assert.Equal(Path.Combine(root, "Basis Server", "BasisServerConsole", "bin", "Release", "net10.0"),
            paths.RuntimeDirectory);
        Assert.Equal(Path.Combine(paths.RuntimeDirectory, "config", "config.xml"), paths.ConfigFile);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("example.org", false)]
    public void IsLocalHost_recognizes_loopback_targets(string host, bool expected)
    {
        Assert.Equal(expected, BasisServerService.IsLocalHost(host));
    }

    [Fact]
    public async Task CanConnect_detects_a_listening_local_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = (ushort)((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(await BasisServerService.CanConnectAsync("127.0.0.1", port, TimeSpan.FromSeconds(1)));
        }
        finally { listener.Stop(); }
    }

    [Theory]
    [InlineData("127.0.0.1", 4296, "", "127.0.0.1:4296")]
    [InlineData("example.org", 5000, "secret", "example.org:5000#secret")]
    [InlineData("::1", 4296, null, "[::1]:4296")]
    public void BuildConnection_uses_the_client_parser_format(string host, ushort port, string? password, string expected)
    {
        Assert.Equal(expected, BasisServerService.BuildConnection(host, port, password));
    }

    [Fact]
    public void Config_round_trip_updates_values_and_preserves_unknown_fields_and_comments()
    {
        using var t = new TempDir();
        var service = new BasisServerService();
        var paths = service.GetPaths(t.CreateDir("Basis"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.ConfigFile)!);
            File.WriteAllText(paths.ConfigFile, """
                <Configuration>
                  <!-- Public server name -->
                  <ServerName>Old</ServerName>
                  <FutureSetting>keep me</FutureSetting>
                </Configuration>
                """);

            var fields = service.LoadConfig(t.Combine("Basis"));
            Assert.Contains(fields, x => x.Name == "ServerName" && x.Description == "Public server name");
            service.SaveConfig(t.Combine("Basis"), fields.Select(x => x.Name == "ServerName" ? x with { Value = "New" } : x));

            var saved = XDocument.Load(paths.ConfigFile).Root!;
            Assert.Equal("New", saved.Element("ServerName")!.Value);
            Assert.Equal("keep me", saved.Element("FutureSetting")!.Value);
            Assert.Contains("Public server name", File.ReadAllText(paths.ConfigFile));
        }
        finally { if (Directory.Exists(paths.RuntimeDirectory)) Directory.Delete(paths.RuntimeDirectory, true); }
    }

    [Fact]
    public void Adds_both_supported_content_file_shapes()
    {
        using var t = new TempDir();
        var service = new BasisServerService();
        var root = t.CreateDir("Basis");
        var paths = service.GetPaths(root);
        try
        {
            var library = service.AddDefaultLibraryItem(root, 1, "https://example/world.bee", "pw");
            var initial = service.AddInitialResource(root, 0, "https://example/prop.bee", "");

            Assert.Equal("BasisDefaultLibraryConfiguration", XDocument.Load(library).Root!.Name.LocalName);
            Assert.Equal("https://example/world.bee", XDocument.Load(library).Root!.Element("Url")!.Value);
            Assert.Equal("BasisLoadableConfiguration", XDocument.Load(initial).Root!.Name.LocalName);
            Assert.Equal("1", XDocument.Load(initial).Root!.Element("QuaternionW")!.Value);
            Assert.Equal(2, service.ListContent(root).Count);
        }
        finally { if (Directory.Exists(paths.RuntimeDirectory)) Directory.Delete(paths.RuntimeDirectory, true); }
    }

    [Fact]
    public void RemoveContent_rejects_files_outside_the_managed_folders()
    {
        using var t = new TempDir();
        var service = new BasisServerService();
        var root = t.CreateDir("Basis");
        var outside = t.WriteFile("outside.xml", "<x/>");
        Assert.Throws<InvalidOperationException>(() => service.RemoveContent(root, new ServerContentFile("outside.xml", outside, true)));
        Assert.True(File.Exists(outside));
    }
}
