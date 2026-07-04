using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UpmGitUrlTests
{
    [Fact]
    public void Parses_plain_https_github()
    {
        var u = UpmGitUrl.Parse("https://github.com/Owner/Repo.git")!;
        Assert.NotNull(u);
        Assert.Equal("github.com", u.Host);
        Assert.Equal("Owner", u.Owner);
        Assert.Equal("Repo", u.Repo);
        Assert.Equal("https://github.com/Owner/Repo.git", u.CloneUrl);
        Assert.Null(u.Ref);
        Assert.Null(u.Path);
        Assert.True(u.IsGitHub);
        Assert.False(u.IsGitLab);
    }

    [Fact]
    public void Adds_dot_git_when_missing()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo")!;
        Assert.Equal("https://github.com/owner/repo.git", u.CloneUrl);
    }

    [Fact]
    public void Parses_path_and_ref()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo.git?path=Packages/com.x#dev")!;
        Assert.Equal("dev", u.Ref);
        Assert.Equal("Packages/com.x", u.Path);
        Assert.Equal("https://github.com/owner/repo.git", u.CloneUrl);
    }

    [Fact]
    public void Url_encoded_path_is_decoded_and_trimmed()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo.git?path=%2FPackages%2Fcom.x%2F")!;
        Assert.Equal("Packages/com.x", u.Path);
    }

    [Fact]
    public void Parses_scp_style_ssh()
    {
        var u = UpmGitUrl.Parse("git@github.com:owner/repo.git")!;
        Assert.Equal("github.com", u.Host);
        Assert.Equal("owner", u.Owner);
        Assert.Equal("repo", u.Repo);
        Assert.Equal("https://github.com/owner/repo.git", u.CloneUrl);
    }

    [Fact]
    public void Parses_ssh_scheme()
    {
        var u = UpmGitUrl.Parse("ssh://git@github.com/owner/repo.git")!;
        Assert.Equal("github.com", u.Host);
        Assert.Equal("owner", u.Owner);
        Assert.Equal("repo", u.Repo);
    }

    [Fact]
    public void Strips_git_plus_prefix()
    {
        var u = UpmGitUrl.Parse("git+https://github.com/owner/repo.git")!;
        Assert.Equal("github.com", u.Host);
        Assert.Equal("owner", u.Owner);
        Assert.Equal("repo", u.Repo);
    }

    [Fact]
    public void Trailing_slash_is_trimmed()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo/")!;
        Assert.Equal("repo", u.Repo);
    }

    [Fact]
    public void Gitlab_subgroups_become_the_owner()
    {
        var u = UpmGitUrl.Parse("https://gitlab.com/group/subgroup/repo.git")!;
        Assert.Equal("gitlab.com", u.Host);
        Assert.Equal("group/subgroup", u.Owner);
        Assert.Equal("repo", u.Repo);
        Assert.True(u.IsGitLab);
        Assert.Equal("group/subgroup/repo", u.Slug);
        Assert.Equal("https://gitlab.com/group/subgroup/repo.git", u.CloneUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty(string? input) => Assert.Null(UpmGitUrl.Parse(input));

    [Fact]
    public void ToManifestUrl_includes_path_and_ref_when_present()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo.git?path=Packages/com.x")!;
        Assert.Equal("https://github.com/owner/repo.git?path=Packages/com.x#main", u.ToManifestUrl("main"));
    }

    [Fact]
    public void ToManifestUrl_subpath_argument_overrides_parsed_path()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo.git?path=Original")!;
        Assert.Equal("https://github.com/owner/repo.git?path=Override#dev", u.ToManifestUrl("dev", "Override"));
    }

    [Fact]
    public void ToManifestUrl_bare_when_no_ref_or_path()
    {
        var u = UpmGitUrl.Parse("https://github.com/owner/repo.git")!;
        Assert.Equal("https://github.com/owner/repo.git", u.ToManifestUrl());
    }
}
