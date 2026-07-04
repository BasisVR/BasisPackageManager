using BasisPM.App.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class DeepLinkTests
{
    [Theory]
    [InlineData("basispm://install?id=x", true)]
    [InlineData("BASISPM://install?id=x", true)]
    [InlineData("http://example.com", false)]
    [InlineData("install?id=x", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsDeepLink_recognises_the_scheme(string? uri, bool expected)
    {
        Assert.Equal(expected, DeepLink.IsDeepLink(uri));
    }

    [Fact]
    public void TryParseInstall_extracts_all_parameters()
    {
        var ok = DeepLink.TryParseInstall(
            "basispm://install?id=com.x&name=Cool%20Pkg&git=https%3A%2F%2Fgithub.com%2Fo%2Fr.git&repo=https://github.com/o/r",
            out var req);

        Assert.True(ok);
        Assert.Equal("com.x", req.Id);
        Assert.Equal("Cool Pkg", req.Name);
        Assert.Equal("https://github.com/o/r.git", req.Git);
        Assert.Equal("https://github.com/o/r", req.Repo);
    }

    [Fact]
    public void TryParseInstall_is_case_insensitive_on_host_and_keys()
    {
        var ok = DeepLink.TryParseInstall("basispm://INSTALL?ID=com.x", out var req);
        Assert.True(ok);
        Assert.Equal("com.x", req.Id);
    }

    [Fact]
    public void TryParseInstall_rejects_a_bundle_link()
    {
        Assert.False(DeepLink.TryParseInstall("basispm://bundle?id=starter", out _));
    }

    [Fact]
    public void TryParseInstall_rejects_a_non_deeplink()
    {
        Assert.False(DeepLink.TryParseInstall("https://example.com", out _));
    }

    [Fact]
    public void TryParseBundle_extracts_the_id()
    {
        Assert.True(DeepLink.TryParseBundle("basispm://bundle?id=basis-starter", out var id));
        Assert.Equal("basis-starter", id);
    }

    [Fact]
    public void TryParseBundle_requires_an_id()
    {
        Assert.False(DeepLink.TryParseBundle("basispm://bundle", out _));
    }

    [Fact]
    public void TryParseBundle_rejects_an_install_link()
    {
        Assert.False(DeepLink.TryParseBundle("basispm://install?id=x", out _));
    }
}
