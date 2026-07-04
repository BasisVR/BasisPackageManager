using System.Reflection;
using BasisPM.Server.Services;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class RepoStatsServiceTests
{
    private static (string? host, string? owner, string? repo) Parse(string url)
    {
        var method = typeof(RepoStatsService).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, new object[] { url })!;
        var type = result.GetType();
        return (
            (string?)type.GetField("Item1")!.GetValue(result),
            (string?)type.GetField("Item2")!.GetValue(result),
            (string?)type.GetField("Item3")!.GetValue(result));
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "github", "owner", "repo")]
    [InlineData("https://github.com/owner/repo.git", "github", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "github", "owner", "repo")]
    [InlineData("https://github.com/owner/repo?tab=readme", "github", "owner", "repo")]
    [InlineData("https://gitlab.com/group/repo", "gitlab", "group", "repo")]
    public void Parse_extracts_host_owner_repo(string url, string host, string owner, string repo)
    {
        Assert.Equal((host, owner, repo), Parse(url));
    }

    [Fact]
    public void Parse_returns_nulls_for_unknown_host()
    {
        Assert.Equal((null, null, null), Parse("https://example.com/owner/repo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FetchAsync_returns_null_for_blank_url(string? url)
    {
        Assert.Null(await new RepoStatsService().FetchAsync(url));
    }

    [Fact]
    public async Task FetchAsync_returns_null_for_unknown_host_without_network()
    {
        Assert.Null(await new RepoStatsService().FetchAsync("https://example.com/owner/repo"));
    }
}
