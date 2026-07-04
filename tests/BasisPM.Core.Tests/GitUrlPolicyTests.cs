using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitUrlPolicyTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("http://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com/owner/repo.git")]
    [InlineData("git://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("https://gitlab.com/group/sub/repo.git")]
    public void IsSafeUrl_accepts_real_fetch_transports(string url) => Assert.True(GitUrlPolicy.IsSafeUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ext::sh -c 'rm -rf /'")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript://alert(1)")]
    [InlineData("ftp://host/repo.git")]
    [InlineData("--upload-pack=/bin/sh")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("/home/user/repo")]
    [InlineData("https://github.com/a b/repo.git")]
    [InlineData("https://github.com/a\nb")]
    public void IsSafeUrl_rejects_dangerous_input(string? url) => Assert.False(GitUrlPolicy.IsSafeUrl(url));

    [Theory]
    [InlineData("https://github.com/owner/repo.git", true)]
    [InlineData("https://www.github.com/owner/repo.git", true)]
    [InlineData("https://gitlab.com/group/repo.git", true)]
    [InlineData("https://www.gitlab.com/group/repo.git", true)]
    [InlineData("http://github.com/owner/repo.git", false)]
    [InlineData("git@github.com:owner/repo.git", false)]
    [InlineData("https://evil.example.com/owner/repo.git", false)]
    [InlineData("ext::sh -c x", false)]
    public void IsHostedGitUrl_only_allows_https_github_or_gitlab(string url, bool expected)
        => Assert.Equal(expected, GitUrlPolicy.IsHostedGitUrl(url));

    [Theory]
    [InlineData("https://basisvr.org", true)]
    [InlineData("http://example.com/x", true)]
    [InlineData("mailto:hi@basisvr.org", true)]
    [InlineData("ftp://host/x", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void IsWebUrl_only_allows_http_https_mailto(string? url, bool expected)
        => Assert.Equal(expected, GitUrlPolicy.IsWebUrl(url));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("main", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("feature/thing", true)]
    [InlineData("a1b2c3d", true)]
    [InlineData("--upload-pack=x", false)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("a\tb", false)]
    [InlineData("a\nb", false)]
    public void IsSafeRef_blocks_option_injection_and_whitespace(string? gitRef, bool expected)
        => Assert.Equal(expected, GitUrlPolicy.IsSafeRef(gitRef));

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("Packages/com.x", true)]
    [InlineData("a/b/c", true)]
    [InlineData("..", false)]
    [InlineData("../escape", false)]
    [InlineData("a/../b", false)]
    [InlineData("a/..\\b", false)]
    [InlineData("/absolute/path", false)]
    public void IsSafeSubPath_stays_inside_the_clone(string? path, bool expected)
        => Assert.Equal(expected, GitUrlPolicy.IsSafeSubPath(path));

    [Fact]
    public void AllowedGitProtocols_constant_is_the_fetch_only_set()
    {
        Assert.Equal("https:http:git:ssh", GitUrlPolicy.AllowedGitProtocols);
    }
}
