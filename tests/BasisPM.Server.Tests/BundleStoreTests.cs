using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class BundleStoreTests
{
    private const string SeedJson = """
    [
      { "id": "starter", "name": "Starter Pack",
        "packages": [ { "id": "com.x", "gitUrl": "https://github.com/x/x.git" } ] },
      { "id": "pro", "name": "Pro Pack", "packages": [] }
    ]
    """;

    [Fact]
    public void LoadSeed_returns_empty_for_missing_or_null()
    {
        Assert.Empty(BundleStore.LoadSeed(null));
        Assert.Empty(BundleStore.LoadSeed(@"C:\nope\bundles.json"));
    }

    [Fact]
    public void All_returns_seeded_bundles()
    {
        using var t = new TempDir();
        var store = new BundleStore(t.WriteFile("bundles.json", SeedJson));
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void Get_is_case_insensitive_and_null_when_absent()
    {
        using var t = new TempDir();
        var store = new BundleStore(t.WriteFile("bundles.json", SeedJson));

        Assert.Equal("Starter Pack", store.Get("STARTER")!.Name);
        Assert.Null(store.Get("missing"));
    }

    [Fact]
    public void Corrupt_seed_yields_empty()
    {
        using var t = new TempDir();
        var store = new BundleStore(t.WriteFile("bundles.json", "{ not json"));
        Assert.Empty(store.All());
    }
}
