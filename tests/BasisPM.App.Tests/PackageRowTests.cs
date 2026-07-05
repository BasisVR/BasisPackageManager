using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackageRowTests
{
    private static CatalogPackageVersion Entry(
        string name = "com.basis.sdk", string display = "Basis SDK", string version = "1.0.0",
        string? unity = "6000.0", string? author = "BasisVR", string? license = null)
        => new()
        {
            Name = name,
            DisplayName = display,
            Version = version,
            Description = "A package.",
            Unity = unity,
            License = license,
            Author = author is null ? null : new CatalogAuthor { Name = author },
        };

    [Fact]
    public void Not_installed_row_shows_install()
    {
        var row = new PackageRow(Entry(), InstalledVersion: null);
        Assert.False(row.IsInstalled);
        Assert.Equal("Install", row.ButtonLabel);
    }

    [Fact]
    public void Installed_row_shows_update()
    {
        var row = new PackageRow(Entry(), InstalledVersion: "1.0.0");
        Assert.True(row.IsInstalled);
        Assert.Equal("Update", row.ButtonLabel);
    }

    [Fact]
    public void Passes_through_catalog_fields()
    {
        var row = new PackageRow(Entry(display: "Basis SDK", version: "2.1.0"), null);
        Assert.Equal("Basis SDK", row.DisplayName);
        Assert.Equal("com.basis.sdk", row.Name);
        Assert.Equal("2.1.0", row.Version);
        Assert.Equal("A package.", row.Description);
    }

    [Fact]
    public void Author_and_unity_presence_flags()
    {
        var withBoth = new PackageRow(Entry(unity: "6000.0", author: "BasisVR"), null);
        Assert.True(withBoth.HasAuthor);
        Assert.Equal("BasisVR", withBoth.Author);
        Assert.True(withBoth.HasUnity);

        var withNeither = new PackageRow(Entry(unity: null, author: null), null);
        Assert.False(withNeither.HasAuthor);
        Assert.Equal("", withNeither.Author);
        Assert.False(withNeither.HasUnity);
    }

    [Fact]
    public void License_presence_flag_and_passthrough()
    {
        var withLicense = new PackageRow(Entry(license: "MIT AND Unlicense"), null);
        Assert.True(withLicense.HasLicense);
        Assert.Equal("MIT AND Unlicense", withLicense.License);

        var noLicense = new PackageRow(Entry(license: null), null);
        Assert.False(noLicense.HasLicense);
    }

    [Theory]
    [InlineData("Basis SDK", "B")]
    [InlineData("  spaced name", "S")]
    [InlineData("", "?")]
    public void Initial_is_the_first_letter_or_question_mark(string display, string expected)
    {
        Assert.Equal(expected, new PackageRow(Entry(display: display), null).Initial);
    }

    [Theory]
    [InlineData("com.unity.2d.sprite", "Unity")]
    [InlineData("com.basis.sdk", "Basis")]
    [InlineData("foo", "Foo")]
    [InlineData("", "Other")]
    public void Owner_is_derived_from_the_package_id(string name, string expectedOwner)
    {
        Assert.Equal(expectedOwner, new PackageRow(Entry(name: name), null).Owner);
    }

    [Fact]
    public void InstalledPackageRow_exposes_its_fields()
    {
        var row = new InstalledPackageRow("com.x", "X Package", "https://github.com/x/x.git", IsFromGit: true);
        Assert.Equal("com.x", row.Name);
        Assert.Equal("X Package", row.DisplayName);
        Assert.Equal("https://github.com/x/x.git", row.Version);
        Assert.True(row.IsFromGit);
    }
}
