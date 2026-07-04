using System.Net;
using System.Net.Http.Json;
using BasisPM.Core.Models;
using BasisPM.Server.Models;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class ApiGetTests : IClassFixture<DisabledSubmissionsFactory>
{
    private const string SeedPackageId = "com.basis.pooltable";
    private const string SeedBundleId = "basis-starter";
    private readonly HttpClient _client;

    public ApiGetTests(DisabledSubmissionsFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Packages_feed_lists_the_seed_package()
    {
        var packages = await _client.GetFromJsonAsync<List<RegistryPackage>>("/packages.json");
        Assert.NotNull(packages);
        Assert.Contains(packages!, p => p.Id == SeedPackageId);
    }

    [Fact]
    public async Task Api_packages_returns_the_registry()
    {
        var packages = await _client.GetFromJsonAsync<List<RegistryPackage>>("/api/packages");
        Assert.Contains(packages!, p => p.Id == SeedPackageId);
    }

    [Fact]
    public async Task Api_package_by_id_found_and_not_found()
    {
        var found = await _client.GetAsync($"/api/packages/{SeedPackageId}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);
        var pkg = await found.Content.ReadFromJsonAsync<RegistryPackage>();
        Assert.Equal(SeedPackageId, pkg!.Id);

        var missing = await _client.GetAsync("/api/packages/com.nope.nope");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Api_packages_search_filters_results()
    {
        var hit = await _client.GetFromJsonAsync<List<RegistryPackage>>("/api/packages?search=pool");
        Assert.Contains(hit!, p => p.Id == SeedPackageId);

        var miss = await _client.GetFromJsonAsync<List<RegistryPackage>>("/api/packages?search=zzz-no-match-zzz");
        Assert.Empty(miss!);
    }

    [Fact]
    public async Task Api_packages_category_filter()
    {
        var worlds = await _client.GetFromJsonAsync<List<RegistryPackage>>("/api/packages?category=Worlds");
        Assert.Contains(worlds!, p => p.Id == SeedPackageId);

        var none = await _client.GetFromJsonAsync<List<RegistryPackage>>("/api/packages?category=NoSuchCategory");
        Assert.Empty(none!);
    }

    [Fact]
    public async Task Api_categories_includes_the_seed_category()
    {
        var categories = await _client.GetFromJsonAsync<List<string>>("/api/categories");
        Assert.Contains("Worlds", categories!);
    }

    [Fact]
    public async Task Catalog_feeds_are_app_compatible()
    {
        foreach (var url in new[] { "/catalog.json", "/api/catalog" })
        {
            var catalog = await _client.GetFromJsonAsync<Catalog>(url);
            Assert.NotNull(catalog);
            Assert.Contains(SeedPackageId, catalog!.Packages.Keys);
        }
    }

    [Fact]
    public async Task Bundle_feeds_list_the_seed_bundle()
    {
        foreach (var url in new[] { "/bundles.json", "/api/bundles" })
        {
            var bundles = await _client.GetFromJsonAsync<List<Bundle>>(url);
            Assert.Contains(bundles!, b => b.Id == SeedBundleId);
        }
    }

    [Fact]
    public async Task Api_bundle_by_id_found_and_not_found()
    {
        var found = await _client.GetAsync($"/api/bundles/{SeedBundleId}");
        Assert.Equal(HttpStatusCode.OK, found.StatusCode);

        var missing = await _client.GetAsync("/api/bundles/no-such-bundle");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Unknown_route_falls_back_to_the_browse_page()
    {
        var res = await _client.GetAsync("/some/unknown/spa/route");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
    }
}
