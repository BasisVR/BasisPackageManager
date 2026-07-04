using Xunit;

namespace BasisPM.Core.Tests;

public sealed class SemVerTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.0.0", 0, 0, 0, null)]
    [InlineData("10.20.30", 10, 20, 30, null)]
    [InlineData("1", 1, 0, 0, null)]
    [InlineData("1.5", 1, 5, 0, null)]
    [InlineData("v2.3.4", 2, 3, 4, null)]
    [InlineData("V2.3.4", 2, 3, 4, null)]
    [InlineData("  1.2.3  ", 1, 2, 3, null)]
    [InlineData("1.2.3-alpha", 1, 2, 3, "alpha")]
    [InlineData("1.2.3-rc.1", 1, 2, 3, "rc.1")]
    [InlineData("1.0.0-0", 1, 0, 0, "0")]
    public void TryParse_accepts_valid(string input, int major, int minor, int patch, string? pre)
    {
        Assert.True(SemVer.TryParse(input, out var v));
        Assert.Equal(new SemVer(major, minor, patch, pre), v);
        Assert.Equal(pre, v.PreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.3")]
    [InlineData("1..3")]
    [InlineData("-1.2.3")]
    public void TryParse_rejects_invalid(string? input)
    {
        Assert.False(SemVer.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid()
    {
        Assert.Throws<FormatException>(() => SemVer.Parse("not-a-version"));
    }

    [Fact]
    public void Parse_returns_value_on_valid()
    {
        Assert.Equal(new SemVer(1, 2, 3), SemVer.Parse("1.2.3"));
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.4", "1.2.3", 1)]
    public void CompareTo_orders_by_numeric_components(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(SemVer.Parse(a).CompareTo(SemVer.Parse(b))));
    }

    [Fact]
    public void CompareTo_prerelease_is_lower_than_release()
    {
        Assert.True(SemVer.Parse("1.0.0-alpha").CompareTo(SemVer.Parse("1.0.0")) < 0);
        Assert.True(SemVer.Parse("1.0.0").CompareTo(SemVer.Parse("1.0.0-alpha")) > 0);
    }

    [Fact]
    public void CompareTo_prereleases_ordered_ordinally()
    {
        Assert.True(SemVer.Parse("1.0.0-alpha").CompareTo(SemVer.Parse("1.0.0-beta")) < 0);
        Assert.Equal(0, SemVer.Parse("1.0.0-rc").CompareTo(SemVer.Parse("1.0.0-rc")));
    }

    [Fact]
    public void CompareTo_null_is_greater()
    {
        Assert.Equal(1, SemVer.Parse("1.0.0").CompareTo(null));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-rc.1", "1.2.3-rc.1")]
    public void ToString_round_trips(string input, string expected)
    {
        Assert.Equal(expected, SemVer.Parse(input).ToString());
    }
}
