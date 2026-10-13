using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed partial class BasisDevServiceTests
{
    [GitFact]
    public async Task A_declined_review_deletes_the_clone_before_manifest_json_ever_points_at_it()
    {
        using var hosted = new HostedRepository("review", "com.review", null);
        using var p = new Project();
        PackageReview? seen = null;
        var manifestDuringReview = new Dictionary<string, string>();
        var filesDuringReview = false;

        var result = await p.Mounts.MountAsync(p.Install, "com.review", hosted.Url + "#v1", review: review =>
        {
            seen = review;
            manifestDuringReview = p.Manifest();
            filesDuringReview = File.Exists(Path.Combine(review.PackageRoot, "package.json"));
            return Task.FromResult(false);
        });

        Assert.False(result.Ok);
        Assert.True(result.Declined);
        Assert.True(filesDuringReview);
        Assert.False(Directory.Exists(p.Combine(".basisdev/com.review")));
        Assert.DoesNotContain("com.review", manifestDuringReview.Keys);
        Assert.DoesNotContain("com.review", p.Manifest().Keys);
        Assert.Null(BasisPackagesStore.Find(p.Unity, "com.review"));
        Assert.Null(BasisDevStore.Read(p.Unity, "com.review"));
        Assert.Equal(hosted.Commit, seen!.Commit);
        Assert.Equal("com.review", seen.Package!.Name);
        Assert.Equal("v1", seen.Source.Ref);
        Assert.Equal(p.Combine(".basisdev/com.review"), seen.PackageRoot);
    }

    [GitFact]
    public async Task An_approved_review_mounts_the_package_as_usual()
    {
        using var hosted = new HostedRepository("approved", "com.approved", "Packages/com.approved");
        using var p = new Project();

        var result = await p.Mounts.MountAsync(p.Install, "com.approved", hosted.Url + "?path=Packages/com.approved#v1",
            review: review => Task.FromResult(review.PackageRoot == p.Combine(".basisdev/com.approved/Packages/com.approved")));

        Assert.True(result.Ok, result.Error);
        Assert.Equal("file:../.basisdev/com.approved/Packages/com.approved", p.Manifest()["com.approved"]);
    }

    [GitFact]
    public async Task A_review_that_fails_leaves_nothing_behind()
    {
        using var hosted = new HostedRepository("throws", "com.throws", null);
        using var p = new Project();

        await Assert.ThrowsAsync<InvalidOperationException>(() => p.Mounts.MountAsync(p.Install, "com.throws", hosted.Url + "#v1",
            review: _ => throw new InvalidOperationException("Add --yes to go ahead without a confirmation prompt.")));

        Assert.False(Directory.Exists(p.Combine(".basisdev/com.throws")));
        Assert.DoesNotContain("com.throws", p.Manifest().Keys);
    }

    [GitFact]
    public async Task Cloning_again_checks_out_the_recorded_commit_when_the_tag_has_moved()
    {
        using var source = new TempDir("upstream");
        GitSandbox.Run(source.Path, "init", "-q", "-b", "main");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "one");
        GitSandbox.Run(source.Path, "add", "-A");
        GitSandbox.Run(source.Path, "commit", "-q", "-m", "reviewed");
        GitSandbox.Run(source.Path, "tag", "v1");
        var recorded = GitSandbox.Run(source.Path, "rev-parse", "HEAD");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "two");
        GitSandbox.Run(source.Path, "commit", "-q", "-am", "swapped");
        GitSandbox.Run(source.Path, "tag", "-f", "v1");
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisPackagesStore.Set(p.Unity, "com.x", new BasisPackageEntry { Version = "1.0.0", Url = source.Path, Ref = "v1", Path = "Packages/com.x", Commit = recorded });

        var cloned = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Reclone);

        Assert.True(cloned.Ok, cloned.Message);
        Assert.Equal(recorded, GitSandbox.Run(p.Combine(".basisdev/com.x"), "rev-parse", "HEAD"));
        Assert.Equal("one", File.ReadAllText(p.Combine(".basisdev/com.x/Packages/com.x/Runtime/Code.cs")).Trim());
        Assert.Contains("has moved on", cloned.Message);
        Assert.Equal(recorded, BasisDevStore.Read(p.Unity, "com.x")!.Upstream.Commit);
    }

    [GitFact]
    public async Task Cloning_again_refuses_when_the_recorded_commit_was_rewritten_away()
    {
        using var source = new TempDir("upstream");
        GitSandbox.Run(source.Path, "init", "-q", "-b", "main");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "one");
        GitSandbox.Run(source.Path, "add", "-A");
        GitSandbox.Run(source.Path, "commit", "-q", "-m", "reviewed");
        var recorded = GitSandbox.Run(source.Path, "rev-parse", "HEAD");
        GitSandbox.Run(source.Path, "checkout", "-q", "--orphan", "rewritten");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "two");
        GitSandbox.Run(source.Path, "add", "-A");
        GitSandbox.Run(source.Path, "commit", "-q", "-m", "history rewritten");
        GitSandbox.Run(source.Path, "branch", "-D", "main");
        GitSandbox.Run(source.Path, "branch", "-m", "main");
        GitSandbox.Run(source.Path, "tag", "v1");
        GitSandbox.Run(source.Path, "reflog", "expire", "--expire=now", "--all");
        GitSandbox.Run(source.Path, "gc", "-q", "--prune=now");
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisPackagesStore.Set(p.Unity, "com.x", new BasisPackageEntry { Version = "1.0.0", Url = source.Path, Ref = "v1", Path = "Packages/com.x", Commit = recorded });

        var cloned = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Reclone);

        Assert.False(cloned.Ok);
        Assert.Contains("rewritten", cloned.Message);
        Assert.False(Directory.Exists(p.Combine(".basisdev/com.x")) && Directory.EnumerateFileSystemEntries(p.Combine(".basisdev/com.x")).Any());
    }
}

public sealed class ExportTreeTests
{
    [GitFact]
    public async Task A_commits_package_folder_is_written_out_without_touching_the_clone()
    {
        using var sandbox = new GitSandbox();
        var first = sandbox.CommitUpstream("one", ("Packages/com.x/package.json", "{ \"name\": \"com.x\" }"), ("Packages/com.x/Runtime/A.cs", "class A {}"), ("README.md", "readme"));
        sandbox.CommitUpstream("two", ("Packages/com.x/Runtime/A.cs", "class Changed {}"));
        using var destination = new TempDir("export");
        var before = GitSandbox.Status(sandbox.Upstream);

        var result = await sandbox.Git.ExportTreeAsync(sandbox.Upstream, first, "Packages/com.x", destination.Path);

        Assert.True(result.Ok, result.Output);
        Assert.Equal("class A {}", File.ReadAllText(Path.Combine(destination.Path, "Runtime", "A.cs")).Trim());
        Assert.True(File.Exists(Path.Combine(destination.Path, "package.json")));
        Assert.False(File.Exists(Path.Combine(destination.Path, "README.md")));
        Assert.Equal(before, GitSandbox.Status(sandbox.Upstream));
        Assert.Equal("class Changed {}", GitSandbox.Read(sandbox.Upstream, "Packages/com.x/Runtime/A.cs").Trim());
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(sandbox.Upstream, ".git"), "basispm-export-*"));
    }

    [GitFact]
    public async Task Unsafe_commits_and_folders_are_refused()
    {
        using var sandbox = new GitSandbox();
        var commit = sandbox.CommitUpstream("one", ("package.json", "{}"));
        using var destination = new TempDir("export");

        Assert.False((await sandbox.Git.ExportTreeAsync(sandbox.Upstream, "--output=x", null, destination.Path)).Ok);
        Assert.False((await sandbox.Git.ExportTreeAsync(sandbox.Upstream, commit, "../outside", destination.Path)).Ok);
        Assert.True((await sandbox.Git.ExportTreeAsync(sandbox.Upstream, commit, null, destination.Path)).Ok);
        Assert.True(File.Exists(Path.Combine(destination.Path, "package.json")));
    }
}
