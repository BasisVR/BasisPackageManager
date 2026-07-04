using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BundleServiceTests
{
    [Fact]
    public void BundlesUrlFor_null_uses_the_default_registry()
    {
        Assert.Equal("https://basisvr.org/packages/bundles.json", BundleService.BundlesUrlFor(null));
    }

    [Fact]
    public void BundlesUrlFor_derives_sibling_of_a_catalog_url()
    {
        Assert.Equal("https://example.com/reg/bundles.json",
            BundleService.BundlesUrlFor("https://example.com/reg/catalog.json"));
    }

    [Fact]
    public void BundlesUrlFor_is_case_insensitive_on_the_leaf()
    {
        Assert.Equal("https://example.com/reg/bundles.json",
            BundleService.BundlesUrlFor("https://example.com/reg/CATALOG.JSON"));
    }

    [Fact]
    public void BundlesUrlFor_unrecognised_url_uses_the_default()
    {
        Assert.Equal("https://basisvr.org/packages/bundles.json",
            BundleService.BundlesUrlFor("https://example.com/reg/something-else.json"));
    }

    [Fact]
    public async Task LoadAsync_returns_the_bundle_feed()
    {
        const string json = """
        [{ "id": "b1", "name": "Bundle One",
           "packages": [{ "id": "com.x", "gitUrl": "https://github.com/x/x.git" }] }]
        """;
        var svc = new BundleService(StubHttpMessageHandler.Always(json));

        var bundles = await svc.LoadAsync("https://example.com/reg/catalog.json");

        Assert.Single(bundles);
        Assert.Equal("b1", bundles[0].Id);
        Assert.Equal("Bundle One", bundles[0].Name);
        Assert.Single(bundles[0].Packages);
        Assert.Equal("com.x", bundles[0].Packages[0].Id);
    }

    [Fact]
    public async Task LoadAsync_returns_empty_on_failure()
    {
        var svc = new BundleService(StubHttpMessageHandler.AlwaysThrows());
        Assert.Empty(await svc.LoadAsync(null));
    }
}
