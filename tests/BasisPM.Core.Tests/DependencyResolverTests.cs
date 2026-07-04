using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class DependencyResolverTests
{
    private static readonly CatalogService Catalogs = new();
    private static DependencyResolver NewResolver() => new(Catalogs);

    private static Catalog Build(params (string Id, string Version, (string, string)[]? Deps)[] entries)
    {
        var catalog = new Catalog { Name = "test" };
        foreach (var (id, version, deps) in entries)
        {
            if (!catalog.Packages.TryGetValue(id, out var pkg))
                catalog.Packages[id] = pkg = new CatalogPackage();
            pkg.Versions[version] = new CatalogPackageVersion
            {
                Name = id,
                Version = version,
                Url = $"https://github.com/x/{id}.git",
                Dependencies = deps?.ToDictionary(d => d.Item1, d => d.Item2),
            };
        }
        return catalog;
    }

    [Fact]
    public void Resolves_transitive_dependencies_from_the_embedded_catalog()
    {
        var catalog = CatalogService.LoadEmbedded();
        var result = NewResolver().Resolve(catalog, new[] { ("com.basis.sdk", "^0.1.0") });

        Assert.True(result.Ok, string.Join(" | ", result.Missing.Concat(result.Conflicts)));
        Assert.Contains("com.basis.sdk", result.Resolved.Keys);
        Assert.Contains("com.basis.framework", result.Resolved.Keys);
        Assert.Contains("com.basis.networking", result.Resolved.Keys);
        Assert.Equal("0.1.0", result.Resolved["com.basis.networking"].Version);
    }

    [Fact]
    public void Reports_missing_package()
    {
        var catalog = Build(("a", "1.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("does.not.exist", "*") });

        Assert.False(result.Ok);
        Assert.Single(result.Missing);
        Assert.Contains("does.not.exist", result.Missing[0]);
    }

    [Fact]
    public void Reports_missing_when_no_version_satisfies_the_range()
    {
        var catalog = Build(("a", "1.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", ">=99.0.0") });

        Assert.False(result.Ok);
        Assert.Single(result.Missing);
    }

    [Fact]
    public void Reports_invalid_range_as_a_conflict()
    {
        var catalog = Build(("a", "1.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", "^not-a-version") });

        Assert.False(result.Ok);
        Assert.Single(result.Conflicts);
        Assert.Contains("invalid range", result.Conflicts[0]);
    }

    [Fact]
    public void Picks_the_highest_version_satisfying_a_range()
    {
        var catalog = Build(("a", "1.0.0", null), ("a", "1.4.0", null), ("a", "2.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", "^1.0.0") });

        Assert.True(result.Ok);
        Assert.Equal("1.4.0", result.Resolved["a"].Version);
    }

    [Fact]
    public void Upgrades_when_a_later_request_needs_a_newer_incompatible_major()
    {
        var catalog = Build(("a", "1.0.0", null), ("a", "2.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", "^1.0.0"), ("a", "^2.0.0") });

        Assert.True(result.Ok);
        Assert.Equal("2.0.0", result.Resolved["a"].Version);
    }

    [Fact]
    public void Keeps_the_pin_when_it_already_satisfies_a_looser_later_request()
    {
        var catalog = Build(("a", "1.0.0", null), ("a", "2.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", "^1.0.0"), ("a", ">=1.0.0") });

        Assert.True(result.Ok);
        Assert.Equal("1.0.0", result.Resolved["a"].Version);
    }

    [Fact]
    public void Reports_conflict_when_a_later_request_is_incompatible_and_older()
    {
        var catalog = Build(("a", "1.0.0", null), ("a", "2.0.0", null));
        var result = NewResolver().Resolve(catalog, new[] { ("a", "^2.0.0"), ("a", "^1.0.0") });

        Assert.False(result.Ok);
        Assert.Single(result.Conflicts);
        Assert.Contains("already pinned", result.Conflicts[0]);
    }

    [Fact]
    public void Empty_request_resolves_to_nothing()
    {
        var result = NewResolver().Resolve(Build(("a", "1.0.0", null)), Array.Empty<(string, string)>());
        Assert.True(result.Ok);
        Assert.Empty(result.Resolved);
    }
}
