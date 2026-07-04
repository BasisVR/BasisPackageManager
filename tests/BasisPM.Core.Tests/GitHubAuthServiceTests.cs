using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitHubAuthServiceTests
{
    [Fact]
    public void SetPersonalAccessToken_tracks_presence()
    {
        var svc = new GitHubAuthService();
        Assert.False(svc.HasPersonalAccessToken);

        svc.SetPersonalAccessToken("ghp_token");
        Assert.True(svc.HasPersonalAccessToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SetPersonalAccessToken_treats_blank_as_none(string? pat)
    {
        var svc = new GitHubAuthService();
        svc.SetPersonalAccessToken("real");
        svc.SetPersonalAccessToken(pat);
        Assert.False(svc.HasPersonalAccessToken);
    }

    [Fact]
    public async Task GetTokenAsync_falls_back_to_the_pat_when_gh_is_absent()
    {
        var svc = new GitHubAuthService();
        svc.SetPersonalAccessToken("  ghp_trimmed  ");

        if (!svc.GhAvailable)
            Assert.Equal("ghp_trimmed", await svc.GetTokenAsync());
    }
}
