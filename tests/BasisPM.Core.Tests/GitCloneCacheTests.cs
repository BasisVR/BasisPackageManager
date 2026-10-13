using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitCloneCacheTests
{
    [Fact]
    public void MirrorPathFor_maps_urls_to_one_folder_per_repository()
    {
        using var t = new TempDir("cache");
        var cache = new GitCloneCache(t.Combine("cache"));
        var https = cache.MirrorPathFor("https://github.com/BasisVR/Basis.git");
        Assert.Equal(Path.Combine(cache.Root, "github.com", "basisvr", "basis.git"), https);
        Assert.Equal(https, cache.MirrorPathFor("git@github.com:BasisVR/Basis.git"));
        Assert.Equal(https, cache.MirrorPathFor("https://github.com/basisvr/basis"));
        Assert.Equal(Path.Combine(cache.Root, "gitlab.com", "group", "sub", "repo.git"), cache.MirrorPathFor("https://gitlab.com/Group/Sub/Repo.git"));
        Assert.Equal(Path.Combine(cache.Root, "example.org", "Team", "Tools.git"), cache.MirrorPathFor("https://example.org/Team/Tools.git"));
        var local = cache.MirrorPathFor(t.Path);
        Assert.StartsWith(Path.Combine(cache.Root, "local") + Path.DirectorySeparatorChar, local);
        Assert.EndsWith(".git", local);
        Assert.Equal(local, cache.MirrorPathFor(t.Path + Path.DirectorySeparatorChar));
    }

    [GitFact]
    public async Task A_clone_through_the_cache_makes_a_mirror_and_a_standalone_clone()
    {
        using var sandbox = new GitSandbox();
        var first = sandbox.CommitUpstream("first", ("package.json", "{ \"name\": \"com.test.cached\" }\n"));
        var cache = new GitCloneCache(sandbox.Combine("cache"));
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };

        var dest = sandbox.Combine("clone");
        var result = await git.CloneAsync(sandbox.Upstream, dest, "developer");

        Assert.True(result.Ok, result.Output);
        var mirror = cache.MirrorPathFor(sandbox.Upstream);
        Assert.True(GitCloneCache.IsMirror(mirror));
        Assert.Equal(first, GitSandbox.Run(dest, "rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(dest, ".git", "objects", "info", "alternates")) && File.ReadAllText(Path.Combine(dest, ".git", "objects", "info", "alternates")).Trim().Length > 0);
        Assert.False(File.Exists(mirror + GitCloneCache.LockSuffix));

        FileRetry.DeleteDirectory(cache.Root);
        Assert.Equal(first, GitSandbox.Run(dest, "rev-parse", "HEAD"));
        GitSandbox.Run(dest, "fsck", "--no-dangling");
        Assert.Equal("{ \"name\": \"com.test.cached\" }", GitSandbox.Read(dest, "package.json").Trim());
    }

    [GitFact]
    public async Task The_next_clone_refreshes_the_mirror_first()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("first", ("a.txt", "one\n"));
        var cache = new GitCloneCache(sandbox.Combine("cache"));
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };
        Assert.True((await git.CloneAsync(sandbox.Upstream, sandbox.Combine("one"), null)).Ok);

        var second = sandbox.CommitUpstream("second", ("a.txt", "two\n"));
        GitSandbox.Run(sandbox.Upstream, "tag", "v1.0.0");
        var lines = new List<string>();
        var result = await git.CloneAtAsync(sandbox.Upstream, sandbox.Combine("two"), "v1.0.0", lines.Add);

        Assert.True(result.Ok, result.Output);
        Assert.Equal(second, GitSandbox.Run(sandbox.Combine("two"), "rev-parse", "HEAD"));
        Assert.Equal(second, GitSandbox.Run(sandbox.Upstream, "--git-dir=" + cache.MirrorPathFor(sandbox.Upstream), "rev-parse", "refs/tags/v1.0.0"));
        Assert.Single(cache.ListMirrors());
    }

    [GitFact]
    public async Task A_sparse_clone_through_the_cache_still_leaves_folders_out()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("layout", (".gitattributes", "* -text\n"), ("Basis/readme.txt", "basis\n"), ("Basis Server/server.txt", "server\n"));
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = new GitCloneCache(sandbox.Combine("cache")) };
        var dest = sandbox.Combine("sparse");

        var result = await git.CloneAsync(sandbox.Upstream, dest, "developer", null, default, new[] { "/*", "!/Basis Server/" });

        Assert.True(result.Ok, result.Output);
        Assert.True(File.Exists(Path.Combine(dest, "Basis", "readme.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "Basis Server")));
        Assert.Equal("", GitSandbox.Status(dest));
    }

    [GitFact]
    public async Task A_disabled_cache_clones_without_a_mirror()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("first", ("a.txt", "one\n"));
        var cache = new GitCloneCache(sandbox.Combine("cache"), () => false);
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };

        Assert.True((await git.CloneAsync(sandbox.Upstream, sandbox.Combine("clone"), null)).Ok);

        Assert.False(Directory.Exists(cache.Root));
    }

    [GitFact]
    public async Task A_broken_mirror_is_replaced()
    {
        using var sandbox = new GitSandbox();
        var first = sandbox.CommitUpstream("first", ("a.txt", "one\n"));
        var cache = new GitCloneCache(sandbox.Combine("cache"));
        var mirror = cache.MirrorPathFor(sandbox.Upstream);
        Directory.CreateDirectory(Path.Combine(mirror, "objects"));
        Directory.CreateDirectory(Path.Combine(mirror, "refs"));
        File.WriteAllText(Path.Combine(mirror, "HEAD"), "not a ref\n");
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };

        var result = await git.CloneAsync(sandbox.Upstream, sandbox.Combine("clone"), null);

        Assert.True(result.Ok, result.Output);
        Assert.Equal(first, GitSandbox.Run(sandbox.Combine("clone"), "rev-parse", "HEAD"));
        Assert.Equal(first, GitSandbox.Run(sandbox.Upstream, "--git-dir=" + mirror, "rev-parse", "refs/heads/developer"));
    }

    [GitFact]
    public async Task A_busy_mirror_falls_back_to_a_plain_clone()
    {
        using var sandbox = new GitSandbox();
        var first = sandbox.CommitUpstream("first", ("a.txt", "one\n"));
        var cache = new GitCloneCache(sandbox.Combine("cache")) { LockWait = TimeSpan.FromMilliseconds(200) };
        var mirror = cache.MirrorPathFor(sandbox.Upstream);
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };

        using (var held = await cache.AcquireAsync(mirror, TimeSpan.Zero))
        {
            Assert.NotNull(held);
            var result = await git.CloneAsync(sandbox.Upstream, sandbox.Combine("clone"), null);
            Assert.True(result.Ok, result.Output);
        }

        Assert.Equal(first, GitSandbox.Run(sandbox.Combine("clone"), "rev-parse", "HEAD"));
        Assert.False(GitCloneCache.IsMirror(mirror));
    }

    [GitFact]
    public async Task An_unreachable_url_still_fails_cleanly()
    {
        using var sandbox = new GitSandbox();
        var cache = new GitCloneCache(sandbox.Combine("cache"));
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };
        var missing = sandbox.CreateBareRepository("missing");
        Directory.Delete(missing, recursive: true);
        Directory.CreateDirectory(missing);
        var dest = sandbox.Combine("clone");

        var result = await git.CloneAsync(missing, dest, null);

        Assert.False(result.Ok);
        Assert.Empty(cache.ListMirrors());
        Assert.False(Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any());
    }

    [GitFact]
    public async Task Clear_removes_mirrors_and_skips_busy_ones()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("first", ("a.txt", new string('x', 4000)));
        var other = sandbox.CreateBareRepository("other.git");
        GitSandbox.Run(sandbox.Upstream, "push", "-q", other, "developer");
        var cache = new GitCloneCache(sandbox.Combine("cache"));
        var git = new GitService(allowLocalRemotes: true, GitRetryPolicy.None) { CloneCache = cache };
        Assert.True((await git.CloneAsync(sandbox.Upstream, sandbox.Combine("one"), null)).Ok);
        Assert.True((await git.CloneAsync(other, sandbox.Combine("two"), "developer")).Ok);
        Assert.Equal(2, cache.ListMirrors().Count);
        Assert.True(cache.MeasureBytes() > 0);

        GitCacheClearResult result;
        using (await cache.AcquireAsync(cache.MirrorPathFor(other), TimeSpan.Zero))
            result = cache.Clear();

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Busy);
        Assert.True(result.Freed > 0);
        Assert.Equal(new[] { cache.MirrorPathFor(other) }, cache.ListMirrors());
        Assert.Equal(0, cache.Clear().Busy);
        Assert.Empty(cache.ListMirrors());
        Assert.Equal(0, cache.MeasureBytes());
    }
}
