using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitRetryTests
{
    [Theory]
    [InlineData("add", "fatal: Unable to create 'C:/r/.git/index.lock': File exists.\n\nAnother git process seems to be running in this repository")]
    [InlineData("checkout", "fatal: Unable to create '/r/.git/index.lock': File exists.")]
    [InlineData("commit", "Another git process seems to be running in this repository, e.g. an editor opened by 'git commit'.")]
    [InlineData("config", "error: could not lock config file .git/config: File exists")]
    [InlineData("fetch", "error: cannot lock ref 'refs/remotes/origin/main': is at 1111 but expected 2222")]
    [InlineData("fetch", "   d891ae2..2d3a348  developer  -> origin/developer\nerror: fetching ref refs/remotes/origin/developer failed: reference already exists")]
    [InlineData("fetch", "error: fetching ref refs/remotes/origin/main failed: incorrect old value provided")]
    [InlineData("update-ref", "fatal: cannot lock ref 'refs/heads/x': Unable to create '/r/.git/refs/heads/x.lock': File exists.")]
    public void A_lock_held_by_another_git_process_is_worth_another_try(string command, string stderr) =>
        Assert.Equal(GitRetryReason.LockBusy, GitRetry.Classify(new[] { command }, stderr));

    [Fact]
    public void A_ref_that_moved_under_update_ref_is_not_retried() =>
        Assert.Equal(GitRetryReason.None, GitRetry.Classify(new[] { "update-ref", "-m", "x", "refs/heads/x", "a", "b" },
            "fatal: cannot lock ref 'refs/heads/x': is at 1111 but expected 2222"));

    [Theory]
    [InlineData("clone", "fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com")]
    [InlineData("clone", "error: RPC failed; curl 56 Recv failure: Connection was reset\nfatal: early EOF\nfatal: index-pack failed")]
    [InlineData("fetch", "fatal: unable to access 'https://gitlab.com/o/r.git/': Failed to connect to gitlab.com port 443 after 21040 ms: Timed out")]
    [InlineData("fetch", "error: RPC failed; HTTP 502 curl 22 The requested URL returned error: 502")]
    [InlineData("ls-remote", "fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 503")]
    [InlineData("ls-remote", "fatal: unable to access 'https://github.com/o/r.git/': gnutls_handshake() failed: The TLS connection was non-properly terminated.")]
    [InlineData("clone", "error: 1234 bytes of body are still expected\nfetch-pack: unexpected disconnect while reading sideband packet")]
    [InlineData("fetch", "fatal: the remote end hung up unexpectedly")]
    [InlineData("clone", "fatal: unable to access 'https://github.com/o/r.git/': HTTP/2 stream 1 was not closed cleanly: INTERNAL_ERROR (err 2)")]
    [InlineData("clone", "fatal: unable to access 'https://github.com/o/r.git/': schannel: failed to receive handshake, SSL/TLS connection failed")]
    [InlineData("fetch", "ssh: connect to host github.com port 22: Connection timed out\nfatal: Could not read from remote repository.")]
    public void Network_blips_are_retried_for_downloads(string command, string stderr) =>
        Assert.Equal(GitRetryReason.Network, GitRetry.Classify(new[] { command, "--progress" }, stderr));

    [Theory]
    [InlineData("clone", "remote: Repository not found.\nfatal: repository 'https://github.com/o/r.git/' not found")]
    [InlineData("fetch", "fatal: Authentication failed for 'https://github.com/o/r.git/'")]
    [InlineData("clone", "fatal: could not read Username for 'https://github.com': terminal prompts disabled")]
    [InlineData("ls-remote", "fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403")]
    [InlineData("clone", "fatal: unable to access 'https://example.com/o/r.git/': The requested URL returned error: 404")]
    [InlineData("clone", "git@github.com: Permission denied (publickey).\nfatal: Could not read from remote repository.")]
    [InlineData("clone", "fatal: destination path 'C:/x' already exists and is not an empty directory.")]
    [InlineData("fetch", "fatal: 'origin' does not appear to be a git repository")]
    [InlineData("clone", "warning: Could not find remote branch nope to clone.\nfatal: Remote branch nope not found in upstream origin")]
    public void Failures_that_would_fail_again_are_not_retried(string command, string stderr) =>
        Assert.Equal(GitRetryReason.None, GitRetry.Classify(new[] { command }, stderr));

    [Theory]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", GitRetryReason.Network)]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Failed to connect to github.com port 443: Connection refused", GitRetryReason.Network)]
    [InlineData("error: RPC failed; curl 56 Recv failure: Connection was reset\nfatal: the remote end hung up unexpectedly", GitRetryReason.None)]
    [InlineData("error: RPC failed; HTTP 500 curl 22 The requested URL returned error: 500", GitRetryReason.None)]
    [InlineData(" ! [rejected] main -> main (stale info)\nerror: failed to push some refs to 'https://github.com/o/r.git'", GitRetryReason.None)]
    public void A_push_is_retried_only_when_it_never_reached_the_server(string stderr, GitRetryReason expected) =>
        Assert.Equal(expected, GitRetry.Classify(new[] { "push", "--progress", "https://github.com/o/r.git", "main" }, stderr));

    [Fact]
    public void Network_errors_from_local_commands_are_not_retried() =>
        Assert.Equal(GitRetryReason.None, GitRetry.Classify(new[] { "status", "--porcelain=v1" }, "fatal: Could not resolve host: github.com"));

    [Fact]
    public void Nothing_on_stderr_means_nothing_to_retry()
    {
        Assert.Equal(GitRetryReason.None, GitRetry.Classify(new[] { "clone" }, ""));
        Assert.Equal(GitRetryReason.None, GitRetry.Classify(new[] { "clone" }, null));
    }

    [Fact]
    public void The_command_is_found_after_config_overrides_and_options()
    {
        Assert.Equal("commit", GitRetry.CommandOf(new[] { "-c", "user.name=x", "-c", "user.email=y", "commit", "-m", "fetch" }));
        Assert.Equal("checkout", GitRetry.CommandOf(new[] { "-c", "advice.detachedHead=false", "checkout", "--detach", "abc" }));
        Assert.Null(GitRetry.CommandOf(new[] { "--version" }));
    }

    [GitFact]
    public async Task A_command_waits_out_an_index_lock_another_process_lets_go_of()
    {
        using var t = new TempDir();
        var git = new GitService(retry: new GitRetryPolicy(Delays(300, 600, 1200, 2400), Array.Empty<TimeSpan>()));
        var repo = t.CreateDir("repo");
        Assert.True((await git.InitAsync(repo)).Ok);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
        var lockFile = Path.Combine(repo, ".git", "index.lock");
        File.WriteAllText(lockFile, "");
        var release = Task.Run(async () => { await Task.Delay(250); File.Delete(lockFile); });

        var add = await git.AddAllAsync(repo);
        await release;

        Assert.True(add.Ok, add.Output);
        Assert.Contains(await git.GetChangesAsync(repo), change => change.Path == "a.txt" && change.Staged);
    }

    [GitFact]
    public async Task A_lock_that_never_clears_still_fails_after_the_last_try()
    {
        using var t = new TempDir();
        var git = new GitService(retry: new GitRetryPolicy(Delays(10, 10), Array.Empty<TimeSpan>()));
        var repo = t.CreateDir("repo");
        Assert.True((await git.InitAsync(repo)).Ok);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(repo, ".git", "index.lock"), "");

        var add = await git.AddAllAsync(repo);

        Assert.False(add.Ok);
        Assert.Contains("index.lock", add.Output);
    }

    [GitFact]
    public async Task A_fetch_reports_each_new_try_while_a_ref_is_locked()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("first", ("a.txt", "a\n"));
        var project = sandbox.CloneProject();
        sandbox.CommitUpstream("second", ("b.txt", "b\n"));
        var lockFile = Path.Combine(project, ".git", "refs", "remotes", "origin", "developer.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        File.WriteAllText(lockFile, "");
        var git = new GitService(allowLocalRemotes: true, new GitRetryPolicy(Delays(50, 50, 50, 50), Array.Empty<TimeSpan>()));
        var lines = new List<string>();

        var fetch = await git.FetchAsync(project, line =>
        {
            lock (lines) lines.Add(line);
            if (line.Contains("trying again", StringComparison.Ordinal)) File.Delete(lockFile);
        });

        Assert.True(fetch.Ok, fetch.Output);
        lock (lines) Assert.Contains(lines, line => line == "Another git process is using the repository, trying again (2 of 5)…");
        Assert.Equal(GitSandbox.Run(sandbox.Upstream, "rev-parse", "HEAD"), GitSandbox.Run(project, "rev-parse", "refs/remotes/origin/developer"));
    }

    [GitFact]
    public async Task Reading_the_status_never_rewrites_the_index()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("first", ("a.txt", "a\n"));
        var project = sandbox.CloneProject();
        var index = Path.Combine(project, ".git", "index");
        File.SetLastWriteTimeUtc(Path.Combine(project, "a.txt"), DateTime.UtcNow.AddHours(-1));
        var before = File.ReadAllBytes(index);

        Assert.Empty(await sandbox.Git.GetChangesAsync(project));
        Assert.Empty(await sandbox.Git.GetWorkingEntriesAsync(project));
        Assert.Equal(before, File.ReadAllBytes(index));

        GitSandbox.Status(project);
        Assert.NotEqual(before, File.ReadAllBytes(index));
    }

    private static TimeSpan[] Delays(params int[] milliseconds) => milliseconds.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray();
}
