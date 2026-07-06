using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitServiceTests
{
    private static async Task<(GitService git, string repo)> InitRepoAsync(TempDir t)
    {
        var git = new GitService();
        var repo = t.CreateDir("repo");
        var init = await git.InitAsync(repo);
        Assert.True(init.Ok, init.Output);
        File.AppendAllText(Path.Combine(repo, ".git", "config"),
            "\n[user]\n\tname = Tester\n\temail = tester@example.com\n");
        return (git, repo);
    }

    private static async Task CommitFileAsync(GitService git, string repo, string name, string content, string message)
    {
        File.WriteAllText(Path.Combine(repo, name), content);
        Assert.True((await git.AddAllAsync(repo)).Ok);
        var commit = await git.CommitAsync(repo, message, "Tester", "tester@example.com");
        Assert.True(commit.Ok, commit.Output);
    }

    [Fact]
    public void IsGitRepo_is_false_for_a_plain_folder()
    {
        using var t = new TempDir();
        Assert.False(new GitService().IsGitRepo(t.Path));
    }

    [Fact]
    public async Task CloneAt_refuses_an_unsafe_ref_without_touching_git()
    {
        using var t = new TempDir();
        var result = await new GitService().CloneAtAsync("https://github.com/o/r.git", t.Combine("dest"), "--evil-ref");
        Assert.False(result.Ok);
        Assert.Contains("ref is not valid", result.Output);
    }

    [GitFact]
    public async Task Init_commit_reports_branch_and_commit()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        Assert.True(git.IsGitRepo(repo));

        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");

        Assert.Equal("main", await git.GetBranchAsync(repo));
        Assert.NotEmpty(await git.GetShortCommitAsync(repo));
    }

    [GitFact]
    public async Task Status_reports_clean_then_untracked_then_modified()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");

        Assert.True((await git.GetStatusAsync(repo)).IsClean);

        File.WriteAllText(Path.Combine(repo, "new.txt"), "brand new\n");
        var untracked = await git.GetStatusAsync(repo);
        Assert.Contains(untracked.Changes, c => c.Path == "new.txt" && c.Kind == GitChangeKind.Untracked);

        File.WriteAllText(Path.Combine(repo, "a.txt"), "changed\n");
        var modified = await git.GetStatusAsync(repo);
        Assert.Contains(modified.Changes, c => c.Path == "a.txt" && c.Kind == GitChangeKind.Modified);
    }

    [GitFact]
    public async Task Diff_shows_the_new_content_of_a_modified_file()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "original\n", "initial");

        File.WriteAllText(Path.Combine(repo, "a.txt"), "brand-new-line\n");
        var change = (await git.GetStatusAsync(repo)).Changes.First(c => c.Path == "a.txt");
        var diff = await git.GetDiffAsync(repo, change);

        Assert.Contains("brand-new-line", diff);
    }

    [GitFact]
    public async Task CheckoutNewBranch_switches_branch_and_rejects_bad_names()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");

        var ok = await git.CheckoutNewBranchAsync(repo, "feature/thing");
        Assert.True(ok.Ok, ok.Output);
        Assert.Equal("feature/thing", await git.GetBranchAsync(repo));

        var bad = await git.CheckoutNewBranchAsync(repo, "--evil");
        Assert.False(bad.Ok);
    }

    [GitFact]
    public async Task SetRemote_then_GetRemoteUrl_round_trips_and_updates()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);

        Assert.True((await git.SetRemoteAsync(repo, "origin", "https://github.com/o/r.git")).Ok);
        Assert.Equal("https://github.com/o/r.git", await git.GetRemoteUrlAsync(repo, "origin"));

        Assert.True((await git.SetRemoteAsync(repo, "origin", "https://github.com/o/r2.git")).Ok);
        Assert.Equal("https://github.com/o/r2.git", await git.GetRemoteUrlAsync(repo, "origin"));

        Assert.Null(await git.GetRemoteUrlAsync(repo, "no-such-remote"));
    }

    [GitFact]
    public async Task Tag_creates_an_annotated_tag()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");

        var tag = await git.TagAsync(repo, "v1.0.0", "release one");
        Assert.True(tag.Ok, tag.Output);
    }

    [GitFact]
    public async Task Clone_refuses_an_unsafe_transport()
    {
        using var t = new TempDir();
        var result = await new GitService().CloneAsync("ext::sh -c evil", t.Combine("dest"), null);
        Assert.False(result.Ok);
        Assert.Contains("unsupported or unsafe", result.Output);
    }

    [GitFact]
    public async Task ListBranches_returns_local_branches_including_slashed_names()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");

        Assert.True((await git.CheckoutNewBranchAsync(repo, "develop")).Ok);
        Assert.True((await git.CheckoutNewBranchAsync(repo, "feature/cool-thing")).Ok);

        var branches = await git.ListBranchesAsync(repo);

        Assert.Contains("main", branches);
        Assert.Contains("develop", branches);
        Assert.Contains("feature/cool-thing", branches);   // slash preserved, not truncated to "cool-thing"
    }

    [GitFact]
    public async Task Checkout_switches_to_an_existing_branch_and_rejects_bad_names()
    {
        using var t = new TempDir();
        var (git, repo) = await InitRepoAsync(t);
        await CommitFileAsync(git, repo, "a.txt", "hello\n", "initial");
        Assert.True((await git.CheckoutNewBranchAsync(repo, "develop")).Ok);

        // Switch back to main, then to the existing branch by name.
        Assert.True((await git.CheckoutAsync(repo, "main")).Ok);
        Assert.Equal("main", await git.GetBranchAsync(repo));
        Assert.True((await git.CheckoutAsync(repo, "develop")).Ok);
        Assert.Equal("develop", await git.GetBranchAsync(repo));

        Assert.False((await git.CheckoutAsync(repo, "--evil")).Ok);
    }
}
