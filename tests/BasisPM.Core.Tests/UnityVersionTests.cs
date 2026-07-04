using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityVersionTests
{
    [Theory]
    [InlineData("6000.0.25f1", 6000, 0, 25, 'f', 1)]
    [InlineData("2022.3.10a5", 2022, 3, 10, 'a', 5)]
    [InlineData("2021.3.0b2", 2021, 3, 0, 'b', 2)]
    [InlineData("6000.0.25", 6000, 0, 25, 'f', 0)]
    [InlineData("6000.0.25F1", 6000, 0, 25, 'f', 1)]
    public void TryParse_accepts_valid(string input, int maj, int min, int patch, char channel, int build)
    {
        Assert.True(UnityVersion.TryParse(input, out var v));
        Assert.Equal(maj, v.Major);
        Assert.Equal(min, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(channel, v.Channel);
        Assert.Equal(build, v.Build);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("6000.0")]
    [InlineData("6000")]
    [InlineData("abc.def.ghi")]
    [InlineData("6000.x.1")]
    [InlineData("6000.0.f1")]
    public void TryParse_rejects_invalid(string? input)
    {
        Assert.False(UnityVersion.TryParse(input, out _));
    }

    [Fact]
    public void Parse_throws_on_invalid() => Assert.Throws<FormatException>(() => UnityVersion.Parse("6000.0"));

    [Theory]
    [InlineData("6000.0.24f1", "6000.0.25f1", -1)]
    [InlineData("6000.0.25f1", "6000.0.25f1", 0)]
    [InlineData("6000.1.0f1", "6000.0.99f1", 1)]
    [InlineData("6001.0.0f1", "6000.9.9f1", 1)]
    public void CompareTo_orders_by_components(string a, string b, int sign)
    {
        Assert.Equal(sign, Math.Sign(UnityVersion.Parse(a).CompareTo(UnityVersion.Parse(b))));
    }

    [Fact]
    public void Channel_rank_orders_alpha_beta_x_f_p()
    {
        var order = new[] { "6000.0.1a1", "6000.0.1b1", "6000.0.1x1", "6000.0.1f1", "6000.0.1p1" }
            .Select(UnityVersion.Parse).ToList();
        for (var i = 1; i < order.Count; i++)
            Assert.True(order[i - 1].CompareTo(order[i]) < 0, $"{order[i - 1]} should sort before {order[i]}");
    }

    [Fact]
    public void Build_number_breaks_ties_within_channel()
    {
        Assert.True(UnityVersion.Parse("6000.0.1f1").CompareTo(UnityVersion.Parse("6000.0.1f2")) < 0);
    }

    [Fact]
    public void CompareTo_null_is_greater()
    {
        Assert.Equal(1, UnityVersion.Parse("6000.0.1f1").CompareTo(null));
    }

    [Fact]
    public void Unknown_channel_parses_with_negative_rank()
    {
        Assert.True(UnityVersion.TryParse("6000.0.1z1", out var v));
        Assert.Equal('z', v.Channel);
        Assert.Equal(-1, v.ChannelRank);
    }
}
