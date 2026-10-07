using System.Net;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ContributeServiceTests
{
    private static async Task<(ContributeService svc, GitService git, string repo)> InitRepoAsync(TempDir t)
    {
        var git = new GitService();
        var repo = t.CreateDir("repo");
        var init = await git.InitAsync(repo);
        Assert.True(init.Ok, init.Output);
        var api = new GitHubApiService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound));
        return (new ContributeService(git, api), git, repo);
    }

    [GitFact]
    public async Task GetUpstream_reads_the_github_origin()
    {
        using var t = new TempDir();
        var (svc, git, repo) = await InitRepoAsync(t);
        Assert.True((await git.SetRemoteAsync(repo, "origin", "https://github.com/BasisVR/com.basis.pooltable.git")).Ok);

        var upstream = await svc.GetUpstreamAsync(repo);

        Assert.NotNull(upstream);
        Assert.Equal("BasisVR", upstream!.Owner);
        Assert.Equal("com.basis.pooltable", upstream.Repo);
    }

    [GitFact]
    public async Task GetUpstream_is_null_for_a_non_github_origin()
    {
        using var t = new TempDir();
        var (svc, git, repo) = await InitRepoAsync(t);
        Assert.True((await git.SetRemoteAsync(repo, "origin", "https://gitlab.com/group/sub/repo.git")).Ok);

        Assert.Null(await svc.GetUpstreamAsync(repo));
    }

    [GitFact]
    public async Task GetUpstream_is_null_without_an_origin()
    {
        using var t = new TempDir();
        var (svc, _, repo) = await InitRepoAsync(t);

        Assert.Null(await svc.GetUpstreamAsync(repo));
    }
}
