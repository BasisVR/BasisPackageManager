using Xunit;

namespace BasisPM.Core.Tests;

public sealed class SemVerRangeTests
{
    private static bool Sat(string range, string version) =>
        SemVerRange.Parse(range).Satisfies(SemVer.Parse(version));

    [Theory]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("   ")]
    public void Wildcard_matches_everything(string range)
    {
        Assert.True(Sat(range, "0.0.0"));
        Assert.True(Sat(range, "99.99.99"));
    }

    [Theory]
    [InlineData("^1.2.3", "1.2.3", true)]
    [InlineData("^1.2.3", "1.9.9", true)]
    [InlineData("^1.2.3", "1.2.2", false)]
    [InlineData("^1.2.3", "2.0.0", false)]
    [InlineData("^0.1.0", "0.1.0", true)]
    [InlineData("^0.1.0", "0.9.0", true)]
    [InlineData("^0.1.0", "1.0.0", false)]
    [InlineData("^0.1.0", "0.0.9", false)]
    public void Caret_pins_major(string range, string version, bool expected)
    {
        Assert.Equal(expected, Sat(range, version));
    }

    [Theory]
    [InlineData("~1.2.3", "1.2.3", true)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]
    [InlineData("~1.2.3", "1.2.2", false)]
    public void Tilde_pins_major_and_minor(string range, string version, bool expected)
    {
        Assert.Equal(expected, Sat(range, version));
    }

    [Theory]
    [InlineData(">=1.0.0", "1.0.0", true)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData("<=1.0.0", "1.0.0", true)]
    [InlineData("<=1.0.0", "1.0.1", false)]
    [InlineData(">1.0.0", "1.0.1", true)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<1.0.0", "0.9.9", true)]
    [InlineData("<1.0.0", "1.0.0", false)]
    public void Comparison_operators(string range, string version, bool expected)
    {
        Assert.Equal(expected, Sat(range, version));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]
    [InlineData("1.2.3", "1.2.4", false)]
    public void Bare_version_is_exact(string range, string version, bool expected)
    {
        Assert.Equal(expected, Sat(range, version));
    }

    [Fact]
    public void Whitespace_is_trimmed()
    {
        Assert.True(Sat("  ^1.0.0  ", "1.5.0"));
    }

    [Fact]
    public void ToString_returns_original_spec()
    {
        Assert.Equal("^1.2.3", SemVerRange.Parse("^1.2.3").ToString());
    }

    [Theory]
    [InlineData(">=1.0.0 <2.0.0", "1.5.0", true)]
    [InlineData(">=1.0.0 <2.0.0", "2.0.0", false)]
    [InlineData(">= 1.0.0 < 2.0.0", "1.5.0", true)]
    [InlineData("^1.0.0 || ^2.0.0", "2.3.0", true)]
    [InlineData("^1.0.0 || ^2.0.0", "3.0.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("1.x", "1.9.0", true)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("1.x", "2.0.0-beta", false)]
    [InlineData("1.2.*", "1.2.7", true)]
    [InlineData("1.2.*", "1.3.0", false)]
    [InlineData("1.0.0 - 2.0.0", "2.0.0", true)]
    [InlineData("1.0.0 - 2.0.0", "2.0.1", false)]
    public void Compound_ranges(string range, string version, bool expected)
    {
        Assert.Equal(expected, Sat(range, version));
    }

    [Fact]
    public void TryParse_reports_a_non_range_without_throwing()
    {
        Assert.False(SemVerRange.TryParse("https://github.com/o/r.git", out _));
        Assert.False(SemVerRange.TryParse("file:../com.x", out _));
        Assert.True(SemVerRange.TryParse("^1.0.0", out var range));
        Assert.True(range!.Satisfies(SemVer.Parse("1.2.0")));
    }

    [Fact]
    public void Parse_throws_on_garbage_bound()
    {
        Assert.Throws<FormatException>(() => SemVerRange.Parse("^not-a-version"));
    }
}
