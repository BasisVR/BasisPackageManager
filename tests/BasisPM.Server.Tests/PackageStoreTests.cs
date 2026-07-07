using System.Text.Json;
using BasisPM.Server.Models;
using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class PackageStoreTests
{
    private static RegistryPackage Pkg(string id, string name, string category = "Misc",
        string? gitUrl = null, string author = "Someone", int stars = 0, int forks = 0,
        string updated = "", string? description = null, params string[] tags)
        => new()
        {
            Id = id,
            Name = name,
            Category = category,
            GitUrl = gitUrl ?? $"https://github.com/someone/{id}.git",
            Author = author,
            Stars = stars,
            Forks = forks,
            Updated = updated,
            Description = description ?? "",
            Tags = tags.ToList(),
        };

    private static PackageStore StoreWith(TempDir t, params RegistryPackage[] packages)
    {
        var seed = t.WriteFile("seed/packages.json", JsonSerializer.Serialize(packages));
        return new PackageStore(t.Combine("data"), seed);
    }

    [Theory]
    [InlineData("https://github.com/BasisVR/Basis", null, "official")]
    [InlineData("https://github.com/basisvr/basis.git", null, "official")]
    [InlineData("https://github.com/someone/repo", null, "community")]
    [InlineData(null, "https://github.com/BasisVR/x.git", "official")]
    [InlineData("https://gitlab.com/group/repo", null, "community")]
    [InlineData(null, null, "community")]
    public void DeriveSource_marks_only_basisvr_github_as_official(string? repoUrl, string? gitUrl, string expected)
    {
        Assert.Equal(expected, PackageStore.DeriveSource(repoUrl, gitUrl));
    }

    [Fact]
    public void LoadSeed_missing_file_returns_empty()
    {
        Assert.Empty(PackageStore.LoadSeed(null));
        Assert.Empty(PackageStore.LoadSeed(@"C:\does\not\exist.json"));
    }

    [Fact]
    public void Constructor_seeds_then_persists_registry_json()
    {
        using var t = new TempDir();
        var store = StoreWith(t, Pkg("com.a", "A"));

        Assert.Single(store.All());
        Assert.True(File.Exists(t.Combine("data/registry.json")));

        var reopened = new PackageStore(t.Combine("data"), t.Combine("seed/packages.json"));
        Assert.Single(reopened.All());
    }

    [Fact]
    public void Corrupt_registry_falls_back_to_seed()
    {
        using var t = new TempDir();
        var seed = t.WriteFile("seed/packages.json", JsonSerializer.Serialize(new[] { Pkg("com.a", "A") }));
        t.WriteFile("data/registry.json", "{ not valid json");

        var store = new PackageStore(t.Combine("data"), seed);
        Assert.Single(store.All());
    }

    private static PackageStore QueryStore(TempDir t) => StoreWith(t,
        Pkg("com.a.one", "Alpha Tool", "Tools", gitUrl: "https://github.com/BasisVR/a.git", author: "Alice",
            stars: 10, forks: 2, updated: "2026-01-01", tags: "util"),
        Pkg("com.b.two", "Beta World", "Worlds", gitUrl: "https://github.com/bob/b.git", author: "Bob",
            stars: 5, forks: 9, updated: "2026-03-01", tags: new[] { "world", "fun" }),
        Pkg("com.c.three", "Gamma", "Tools", gitUrl: "https://github.com/carol/c.git", author: "Carol",
            stars: 20, forks: 1, updated: "2026-02-01", description: "third has alpha keyword"));

    [Fact]
    public void Query_search_matches_name_description_and_tags()
    {
        using var t = new TempDir();
        var store = QueryStore(t);

        var byKeyword = store.Query("alpha", null, null, null).Select(p => p.Id).ToHashSet();
        Assert.Equal(new[] { "com.a.one", "com.c.three" }.ToHashSet(), byKeyword);

        Assert.Equal(new[] { "com.a.one" }, store.Query("util", null, null, null).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Query_filters_by_source_and_category()
    {
        using var t = new TempDir();
        var store = QueryStore(t);

        Assert.Equal(new[] { "com.a.one" }, store.Query(null, "official", null, null).Select(p => p.Id).ToArray());
        Assert.Equal(new[] { "com.b.two", "com.c.three" }.ToHashSet(),
            store.Query(null, "community", null, null).Select(p => p.Id).ToHashSet());
        Assert.Equal(3, store.Query(null, "all", null, null).Count);

        Assert.Equal(new[] { "com.a.one", "com.c.three" }.ToHashSet(),
            store.Query(null, null, "Tools", null).Select(p => p.Id).ToHashSet());
        Assert.Equal(new[] { "com.b.two" }, store.Query(null, null, "worlds", null).Select(p => p.Id).ToArray());
    }

    [Theory]
    [InlineData("stars", new[] { "com.c.three", "com.a.one", "com.b.two" })]
    [InlineData("forks", new[] { "com.b.two", "com.a.one", "com.c.three" })]
    [InlineData("updated", new[] { "com.b.two", "com.c.three", "com.a.one" })]
    [InlineData("name", new[] { "com.a.one", "com.b.two", "com.c.three" })]
    public void Query_sorts(string sort, string[] expectedOrder)
    {
        using var t = new TempDir();
        var store = QueryStore(t);
        Assert.Equal(expectedOrder, store.Query(null, null, null, sort).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Query_default_sort_is_stars_then_forks()
    {
        using var t = new TempDir();
        var store = QueryStore(t);
        Assert.Equal(new[] { "com.c.three", "com.a.one", "com.b.two" },
            store.Query(null, null, null, null).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Categories_are_distinct_and_sorted()
    {
        using var t = new TempDir();
        var store = QueryStore(t);
        Assert.Equal(new[] { "Tools", "Worlds" }, store.Categories().ToArray());
    }

    [Fact]
    public void Get_is_case_insensitive()
    {
        using var t = new TempDir();
        var store = QueryStore(t);
        Assert.NotNull(store.Get("COM.A.ONE"));
        Assert.Null(store.Get("nope"));
    }

    [Fact]
    public void BuildCatalog_maps_packages_and_skips_those_without_git_url()
    {
        var withGit = Pkg("com.a", "Alpha", gitUrl: "https://github.com/x/a.git");
        withGit.Version = "1.2.3";
        withGit.License = "MIT AND Unlicense";
        withGit.Category = "Rendering";
        withGit.Source = "official";
        withGit.Tags = new List<string> { "shader", "urp" };
        withGit.Stars = 42;
        withGit.Forks = 7;
        withGit.Updated = "2026-01-02";
        var noGit = new RegistryPackage { Id = "com.b", Name = "Beta", GitUrl = null };

        var catalog = PackageStore.BuildCatalog(new[] { withGit, noGit });

        Assert.Contains("com.a", catalog.Packages.Keys);
        Assert.DoesNotContain("com.b", catalog.Packages.Keys);
        var version = catalog.Packages["com.a"].Versions["1.2.3"];
        Assert.Equal("https://github.com/x/a.git", version.Url);
        Assert.Equal("Alpha", version.DisplayName);
        Assert.Equal("MIT AND Unlicense", version.License);
        // Filter/sort metadata must survive the catalog build so the desktop client can filter on it.
        Assert.Equal("Rendering", version.Category);
        Assert.Equal("official", version.Source);
        Assert.Equal(new[] { "shader", "urp" }, version.Tags);
        Assert.Equal(42, version.Stars);
        Assert.Equal(7, version.Forks);
        Assert.Equal("2026-01-02", version.Updated);
    }

    [Fact]
    public void ToCatalog_reflects_the_store_contents()
    {
        using var t = new TempDir();
        var store = StoreWith(t, Pkg("com.a", "A", gitUrl: "https://github.com/x/a.git"));
        Assert.Contains("com.a", store.ToCatalog().Packages.Keys);
    }
}
