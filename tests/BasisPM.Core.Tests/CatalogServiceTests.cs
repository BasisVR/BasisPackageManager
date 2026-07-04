using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class CatalogServiceTests
{
    private const string RemoteCatalogJson = """
    {
      "name": "Remote Catalog",
      "url": "",
      "packages": {
        "com.x": {
          "versions": {
            "1.0.0": { "name": "com.x", "displayName": "X", "version": "1.0.0" },
            "2.0.0": { "name": "com.x", "displayName": "X 2", "version": "2.0.0" }
          }
        }
      }
    }
    """;

    [Fact]
    public void LoadEmbedded_returns_the_baked_in_catalog()
    {
        var catalog = CatalogService.LoadEmbedded();
        Assert.Equal("BasisVR Default Catalog", catalog.Name);
        Assert.Contains("com.basis.sdk", catalog.Packages.Keys);
        Assert.Contains("com.basis.framework", catalog.Packages.Keys);
        Assert.Contains("com.basis.networking", catalog.Packages.Keys);
    }

    [Fact]
    public async Task LoadAsync_returns_remote_catalog_when_reachable()
    {
        var svc = new CatalogService(StubHttpMessageHandler.Always(RemoteCatalogJson));
        var catalog = await svc.LoadAsync("https://example.com/catalog.json");
        Assert.Equal("Remote Catalog", catalog.Name);
        Assert.Contains("com.x", catalog.Packages.Keys);
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_embedded_when_network_fails()
    {
        var svc = new CatalogService(StubHttpMessageHandler.AlwaysThrows());
        var catalog = await svc.LoadAsync("https://example.com/catalog.json");
        Assert.Equal("BasisVR Default Catalog", catalog.Name);
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_embedded_on_server_error()
    {
        var svc = new CatalogService(StubHttpMessageHandler.AlwaysStatus(System.Net.HttpStatusCode.InternalServerError));
        var catalog = await svc.LoadAsync(null);
        Assert.Equal("BasisVR Default Catalog", catalog.Name);
    }

    [Fact]
    public void AllLatest_picks_the_highest_version_per_package()
    {
        var svc = new CatalogService();
        var catalog = new Catalog();
        catalog.Packages["com.x"] = new CatalogPackage
        {
            Versions =
            {
                ["1.0.0"] = new CatalogPackageVersion { Name = "com.x", Version = "1.0.0" },
                ["2.3.0"] = new CatalogPackageVersion { Name = "com.x", Version = "2.3.0" },
                ["2.1.0"] = new CatalogPackageVersion { Name = "com.x", Version = "2.1.0" },
            },
        };

        var latest = svc.AllLatest(catalog).ToList();
        Assert.Single(latest);
        Assert.Equal("2.3.0", latest[0].Version);
    }

    [Fact]
    public void AllLatest_ignores_unparseable_version_keys()
    {
        var svc = new CatalogService();
        var catalog = new Catalog();
        catalog.Packages["com.x"] = new CatalogPackage
        {
            Versions = { ["not-a-version"] = new CatalogPackageVersion { Name = "com.x", Version = "x" } },
        };
        Assert.Empty(svc.AllLatest(catalog));
    }

    [Fact]
    public void FindBest_returns_highest_satisfying_version()
    {
        var svc = new CatalogService();
        var catalog = new Catalog();
        catalog.Packages["com.x"] = new CatalogPackage
        {
            Versions =
            {
                ["1.0.0"] = new CatalogPackageVersion { Version = "1.0.0" },
                ["1.5.0"] = new CatalogPackageVersion { Version = "1.5.0" },
                ["2.0.0"] = new CatalogPackageVersion { Version = "2.0.0" },
            },
        };

        Assert.Equal("1.5.0", svc.FindBest(catalog, "com.x", SemVerRange.Parse("^1.0.0"))!.Version);
        Assert.Equal("2.0.0", svc.FindBest(catalog, "com.x", SemVerRange.Parse("*"))!.Version);
        Assert.Null(svc.FindBest(catalog, "com.x", SemVerRange.Parse(">=3.0.0")));
        Assert.Null(svc.FindBest(catalog, "missing", SemVerRange.Parse("*")));
    }

    [Fact]
    public void DefaultCatalogUrl_points_at_the_registry()
    {
        Assert.Equal("https://basisvr.org/packages/catalog.json", CatalogService.DefaultCatalogUrl);
    }
}
