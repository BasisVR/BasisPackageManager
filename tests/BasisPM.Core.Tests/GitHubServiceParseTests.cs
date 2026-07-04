using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitHubServiceParseTests
{
    [Fact]
    public void Parses_owner_slash_repo()
    {
        var loc = GitHubService.Parse("owner/repo");
        Assert.Equal("owner", loc.Owner);
        Assert.Equal("repo", loc.Repo);
        Assert.Null(loc.Branch);
        Assert.Null(loc.Path);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("http://github.com/owner/repo")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("github.com/owner/repo")]
    public void Parses_full_urls_to_owner_repo(string input)
    {
        var loc = GitHubService.Parse(input);
        Assert.Equal("owner", loc.Owner);
        Assert.Equal("repo", loc.Repo);
    }

    [Fact]
    public void Extracts_branch_from_tree_url()
    {
        var loc = GitHubService.Parse("https://github.com/owner/repo/tree/dev");
        Assert.Equal("dev", loc.Branch);
        Assert.Null(loc.Path);
    }

    [Fact]
    public void Extracts_branch_and_subpath_from_tree_url()
    {
        var loc = GitHubService.Parse("https://github.com/owner/repo/tree/dev/Packages/com.x");
        Assert.Equal("dev", loc.Branch);
        Assert.Equal("Packages/com.x", loc.Path);
    }

    [Fact]
    public void Extracts_branch_and_subpath_from_blob_url()
    {
        var loc = GitHubService.Parse("https://github.com/owner/repo/blob/main/sub");
        Assert.Equal("main", loc.Branch);
        Assert.Equal("sub", loc.Path);
    }

    [Fact]
    public void Hash_ref_and_query_path_are_honoured()
    {
        var loc = GitHubService.Parse("owner/repo?path=Sub/Dir#feature");
        Assert.Equal("feature", loc.Branch);
        Assert.Equal("Sub/Dir", loc.Path);
    }

    [Fact]
    public void Explicit_hash_ref_wins_over_tree_segment()
    {
        var loc = GitHubService.Parse("https://github.com/owner/repo/tree/dev#override");
        Assert.Equal("override", loc.Branch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("owner")]
    [InlineData("https://github.com/owner")]
    public void Rejects_input_without_owner_and_repo(string input)
    {
        Assert.Throws<FormatException>(() => GitHubService.Parse(input));
    }

    [Fact]
    public void BuildManifestUrl_bare()
    {
        var url = GitHubService.BuildManifestUrl(new GitHubLocator("owner", "repo", null, null));
        Assert.Equal("https://github.com/owner/repo.git", url);
    }

    [Fact]
    public void BuildManifestUrl_with_path_and_branch()
    {
        var url = GitHubService.BuildManifestUrl(new GitHubLocator("owner", "repo", "dev", "Packages/com.x"));
        Assert.Equal("https://github.com/owner/repo.git?path=Packages/com.x#dev", url);
    }

    [Fact]
    public void Parse_then_BuildManifestUrl_round_trips_a_tree_url()
    {
        var loc = GitHubService.Parse("https://github.com/owner/repo/tree/dev/Packages/com.x");
        Assert.Equal("https://github.com/owner/repo.git?path=Packages/com.x#dev",
            GitHubService.BuildManifestUrl(loc));
    }
}
