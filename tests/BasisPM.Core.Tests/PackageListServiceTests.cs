using System.Net;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageListServiceTests
{
    [Fact]
    public void PackageListsUrlFor_null_uses_the_default_registry()
    {
        Assert.Equal("https://basisvr.org/packages/packagelists.json", PackageListService.PackageListsUrlFor(null));
    }

    [Fact]
    public void PackageListsUrlFor_derives_sibling_of_a_catalog_url()
    {
        Assert.Equal("https://example.com/reg/packagelists.json",
            PackageListService.PackageListsUrlFor("https://example.com/reg/catalog.json"));
    }

    [Fact]
    public void PackageListsUrlFor_is_case_insensitive_on_the_leaf()
    {
        Assert.Equal("https://example.com/reg/packagelists.json",
            PackageListService.PackageListsUrlFor("https://example.com/reg/CATALOG.JSON"));
    }

    [Fact]
    public void PackageListsUrlFor_unrecognised_url_uses_the_default()
    {
        Assert.Equal("https://basisvr.org/packages/packagelists.json",
            PackageListService.PackageListsUrlFor("https://example.com/reg/something-else.json"));
    }

    [Fact]
    public async Task LoadAsync_returns_the_package_list_feed()
    {
        const string json = """
        [{ "id": "b1", "name": "Package List One",
           "packages": [{ "id": "com.x", "gitUrl": "https://github.com/x/x.git" }] }]
        """;
        var svc = new PackageListService(StubHttpMessageHandler.Always(json));

        var packageLists = await svc.LoadAsync("https://example.com/reg/catalog.json");

        Assert.Single(packageLists);
        Assert.Equal("b1", packageLists[0].Id);
        Assert.Equal("Package List One", packageLists[0].Name);
        Assert.Single(packageLists[0].Packages);
        Assert.Equal("com.x", packageLists[0].Packages[0].Id);
    }

    [Fact]
    public async Task LoadAsync_falls_back_to_the_legacy_bundles_feed()
    {
        const string json = """
        [{ "id": "b1", "name": "Package List One",
           "packages": [{ "id": "com.x", "gitUrl": "https://github.com/x/x.git" }] }]
        """;
        // Serve only the legacy bundles.json path: packagelists.json 404s, so the
        // service must fall back to bundles.json to load the feed.
        var http = StubHttpMessageHandler.Route(new[] { ("bundles.json", HttpStatusCode.OK, json) });
        var svc = new PackageListService(http);

        var packageLists = await svc.LoadAsync("https://example.com/reg/catalog.json");

        Assert.Single(packageLists);
        Assert.Equal("b1", packageLists[0].Id);
    }

    [Fact]
    public async Task LoadAsync_returns_empty_on_failure()
    {
        var svc = new PackageListService(StubHttpMessageHandler.AlwaysThrows());
        Assert.Empty(await svc.LoadAsync(null));
    }
}
