using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageTrustTests
{
    private const string Reviewed = "abcdef1234567890abcdef1234567890abcdef12";
    private const string Newer = "1234567890abcdef1234567890abcdef12345678";
    private const string Crawled = "fedcba0987654321fedcba0987654321fedcba09";

    private static Catalog Catalog(params CatalogPackageVersion[] versions) => new()
    {
        Packages = { ["com.example.tool"] = new CatalogPackage { Versions = versions.ToDictionary(v => v.Version) } },
    };

    [Fact]
    public void A_pin_on_any_version_covers_the_whole_package()
    {
        var catalog = Catalog(
            new CatalogPackageVersion { Name = "com.example.tool", Version = "1.0.0", Commit = Reviewed, ReviewedCommit = Reviewed },
            new CatalogPackageVersion { Name = "com.example.tool", Version = "1.1.0", Commit = Crawled });

        var reviewed = PackageTrust.ReviewedCommits(catalog, "COM.EXAMPLE.TOOL", catalog.Packages["com.example.tool"].Versions["1.1.0"]);

        Assert.Equal(new[] { Reviewed }, reviewed);
        Assert.True(PackageTrust.IsTrusted(true, reviewed, Reviewed));
        Assert.False(PackageTrust.IsTrusted(true, reviewed, Crawled));
    }

    [Fact]
    public void Without_any_pin_the_registry_vouches_for_the_repository_only_for_registry_installs()
    {
        var catalog = Catalog(new CatalogPackageVersion { Name = "com.example.tool", Version = "1.1.0", Commit = Crawled });
        var reviewed = PackageTrust.ReviewedCommits(catalog, "com.example.tool", null);

        Assert.Empty(reviewed);
        Assert.True(PackageTrust.IsTrusted(true, reviewed, Newer));
        Assert.False(PackageTrust.IsTrusted(false, reviewed, Newer));
    }

    [Fact]
    public void Pins_from_the_entry_count_and_malformed_ones_are_ignored()
    {
        var entry = new CatalogPackageVersion { Name = "com.example.tool", Version = "2.0.0", ReviewedCommit = Newer[..12] };
        var catalog = Catalog(new CatalogPackageVersion { Name = "com.example.tool", Version = "1.0.0", ReviewedCommit = "--upload-pack=x" });

        var reviewed = PackageTrust.ReviewedCommits(catalog, "com.example.tool", entry);

        Assert.Equal(new[] { Newer[..12] }, reviewed);
        Assert.True(PackageTrust.IsTrusted(true, reviewed, Newer));
        Assert.Empty(PackageTrust.ReviewedCommits(null, null, null));
    }
}
