using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageCompatibilityTests
{
    [Fact]
    public void Package_json_reads_every_shape_of_author_basis_and_dependencies()
    {
        var package = PackageJsonFile.Parse("""
        {
          // comments and trailing commas are fine
          "name": "com.example.tools",
          "version": "1.2.0",
          "unity": "6000.0",
          "unityRelease": "30f1",
          "author": "Jane Doe <jane@example.com> (https://example.com)",
          "dependencies": { "com.unity.mathematics": "1.3.2", "com.example.odd": 2 },
          "vpmDependencies": { "com.basis.sdk": "0.0.1" },
          "gitDependencies": { "com.example.extra": "https://github.com/example/Extra.git#v1.0.0" },
          "basis": { "from": "2026-09-16", "channels": "experimental" },
        }
        """)!;

        Assert.Equal("com.example.tools", package.Name);
        Assert.Equal("Jane Doe", package.Author!.Name);
        Assert.Equal("jane@example.com", package.Author.Email);
        Assert.Equal("https://example.com", package.Author.Url);
        Assert.Equal("2", package.Dependencies!["com.example.odd"]);
        Assert.Equal("0.0.1", package.VpmDependencies!["com.basis.sdk"]);
        Assert.Equal("https://github.com/example/Extra.git#v1.0.0", package.GitDependencies!["com.example.extra"]);
        Assert.Equal("2026-09-16", package.Basis!.From);
        Assert.Equal(new[] { "experimental" }, package.Basis.Channels);
        Assert.Equal(new[] { "com.unity.mathematics", "com.example.odd", "com.basis.sdk", "com.example.extra" }, PackageJsonFile.DependencyIds(package));
    }

    [Fact]
    public void A_plain_basis_string_means_from_and_an_author_object_is_read()
    {
        var package = PackageJsonFile.Parse("""{ "name": "a", "basis": "long-term-support-20260916", "author": { "name": "Studio", "url": "https://studio.example" } }""")!;

        Assert.Equal("long-term-support-20260916", package.Basis!.From);
        Assert.Equal(new DateOnly(2026, 9, 16), PackageCompatibility.ParseBasisDate(package.Basis.From));
        Assert.Equal("Studio", package.Author!.Name);
        Assert.Null(PackageJsonFile.Parse("not json"));
    }

    [Theory]
    [InlineData("2026-09-16", 2026, 9, 16)]
    [InlineData("20260916", 2026, 9, 16)]
    [InlineData("2026.09.16", 2026, 9, 16)]
    [InlineData("long-term-support-20260623", 2026, 6, 23)]
    public void Basis_dates_parse_from_the_usual_spellings(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), PackageCompatibility.ParseBasisDate(text));

    [Theory]
    [InlineData("developer")]
    [InlineData("2026-13-01")]
    [InlineData("0000-01-01")]
    [InlineData("")]
    public void Nonsense_is_not_a_basis_date(string text) => Assert.Null(PackageCompatibility.ParseBasisDate(text));

    [Theory]
    [InlineData("6000.2", null, "6000.0.30f1", CompatibilityProblem.NeedsNewerUnity)]
    [InlineData("6000.0", null, "6000.0.30f1", CompatibilityProblem.None)]
    [InlineData("2022.3", "10f1", "2022.3.9f1", CompatibilityProblem.NeedsNewerUnity)]
    [InlineData("2022.3", "10f1", "2022.3.10f1", CompatibilityProblem.None)]
    [InlineData("2022.3", null, "6000.0.1f1", CompatibilityProblem.None)]
    public void Unity_minimums_compare_major_minor_and_release(string unity, string? release, string project, CompatibilityProblem expected) =>
        Assert.Equal(expected, PackageCompatibility.Check(unity, release, null, ProjectTarget.From(project, null)).Problem);

    [Fact]
    public void Basis_ranges_check_from_and_before()
    {
        var range = new BasisCompatibility { From = "2026-09-16", Before = "2026-12-01" };

        Assert.Equal(CompatibilityProblem.NeedsNewerBasis, PackageCompatibility.Check(null, null, range, ProjectTarget.From(null, new DateOnly(2026, 6, 23))).Problem);
        Assert.Equal(CompatibilityProblem.None, PackageCompatibility.Check(null, null, range, ProjectTarget.From(null, new DateOnly(2026, 9, 16))).Problem);
        Assert.Equal(CompatibilityProblem.NeedsOlderBasis, PackageCompatibility.Check(null, null, range, ProjectTarget.From(null, new DateOnly(2026, 12, 1))).Problem);
        Assert.True(PackageCompatibility.Check(null, null, range, ProjectTarget.Unknown).Ok);
    }

    [Fact]
    public void Channels_match_by_family_or_exact_branch()
    {
        var experimental = new BasisCompatibility { Channels = new() { "experimental" } };

        Assert.Equal(CompatibilityProblem.NeedsOtherBasisChannel, PackageCompatibility.Check(null, null, experimental, ProjectTarget.From(null, null, "developer")).Problem);
        Assert.True(PackageCompatibility.Check(null, null, experimental, ProjectTarget.From(null, null, "experimental-branch-major-changes")).Ok);
        Assert.True(PackageCompatibility.MatchesChannels(new[] { "lts" }, "long-term-support-20260916"));
        Assert.True(PackageCompatibility.MatchesChannels(new[] { "long-term-support" }, "long-term-support-20260623"));
        Assert.True(PackageCompatibility.MatchesChannels(new[] { "my-studio-branch" }, "my-studio-branch"));
        Assert.False(PackageCompatibility.MatchesChannels(new[] { "developer" }, "my-studio-branch"));
        Assert.True(PackageCompatibility.MatchesChannels(new[] { "developer" }, null));
    }

    [Fact]
    public void A_catalog_version_carries_its_compatibility()
    {
        var version = new CatalogPackageVersion { Name = "a", Version = "1.0.0", Unity = "6000.3", Basis = new BasisCompatibility { From = "2026-01-01" } };

        var result = PackageCompatibility.Check(version, ProjectTarget.From("6000.0.30f1", new DateOnly(2026, 6, 1)));

        Assert.Equal(CompatibilityProblem.NeedsNewerUnity, result.Problem);
        Assert.Equal("6000.3", result.Required);
    }
}
