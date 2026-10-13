using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed partial class BasisDevServiceTests
{
    private sealed class Releases : IDisposable
    {
        private static readonly string[] Names = { "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0" };
        private readonly TempDir _temp = new("releases");
        private readonly string?[] _previous;

        public Releases(string id)
        {
            Id = id;
            Url = $"https://gitlab.com/basispm-tests/releases-{Guid.NewGuid():N}.git";
            Source = _temp.CreateDir("source");
            GitSandbox.Run(Source, "init", "-q", "-b", "main");
            GitSandbox.Write(Source, ".gitattributes", "* -text\n");
            Project.WritePackage(Source, id, "one");
            GitSandbox.Run(Source, "add", "-A");
            GitSandbox.Run(Source, "commit", "-q", "-m", "first release");
            GitSandbox.Run(Source, "tag", "v1.0.0");
            _previous = Names.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "url." + Source.Replace('\\', '/') + ".insteadOf");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", Url);
        }

        public string Id { get; }
        public string Url { get; }
        public string Source { get; }

        public string Release(string tag, params (string Path, string? Content)[] files)
        {
            var commit = GitSandbox.Commit(Source, "release " + tag, files);
            GitSandbox.Run(Source, "tag", tag);
            return commit;
        }

        public void Dispose()
        {
            for (var i = 0; i < Names.Length; i++) Environment.SetEnvironmentVariable(Names[i], _previous[i]);
            _temp.Dispose();
        }
    }

    private static PackageUpdateService Updates(Project p)
    {
        var github = new GitHubApiService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound));
        return new PackageUpdateService(p.Git, p.Mounts, p.Registry, new VersionService(github, p.Git), github, new GitHubService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound)));
    }

    private static string Head(string folder) => GitSandbox.Run(folder, "rev-parse", "HEAD");

    [GitFact]
    public async Task Update_moves_a_clean_clone_to_the_new_release_in_place_and_records_it()
    {
        using var releases = new Releases("com.update.clean");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        File.WriteAllText(Path.Combine(clone, ".git", "marker"), "same clone");
        var commit = releases.Release("v1.1.0", ("Runtime/Added.cs", "class Added {}\n"));

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v1.1.0"));

        Assert.Equal(PackageUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(commit, Head(clone));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Added.cs")));
        Assert.True(File.Exists(Path.Combine(clone, ".git", "marker")));
        Assert.Equal("file:../.basisdev/" + releases.Id, p.Manifest()[releases.Id]);
        var sidecar = BasisDevStore.Read(p.Unity, releases.Id)!;
        Assert.Equal("v1.1.0", sidecar.Upstream.Ref);
        Assert.Equal(commit, sidecar.Upstream.Commit);
        Assert.Equal(releases.Url + "#v1.1.0", sidecar.Manifest.Original);
        var entry = BasisPackagesStore.Find(p.Unity, releases.Id)!;
        Assert.Equal("v1.1.0", entry.Ref);
        Assert.Equal(commit, entry.Commit);
        Assert.Equal(releases.Url + "#v1.1.0", p.Registry.Find(p.Unity, releases.Id)!.OriginalManifestValue);
    }

    [GitFact]
    public async Task Update_keeps_edits_that_do_not_touch_the_same_lines()
    {
        using var releases = new Releases("com.update.edits");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        File.WriteAllText(Path.Combine(clone, "Runtime", "Code.cs"), "mine\n");
        File.WriteAllText(Path.Combine(clone, "Runtime", "Local.cs"), "class Local {}\n");
        var commit = releases.Release("v1.1.0", ("Runtime/Added.cs", "class Added {}\n"));

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v1.1.0"));

        Assert.Equal(PackageUpdateOutcome.Updated, result.Outcome);
        Assert.True(result.KeptLocalChanges);
        Assert.Equal(commit, Head(clone));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(clone, "Runtime", "Code.cs")));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Local.cs")));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Added.cs")));
        Assert.Equal("", GitSandbox.Run(clone, "stash", "list"));
    }

    [GitFact]
    public async Task Update_with_conflicting_edits_leaves_the_clone_exactly_as_it_was()
    {
        using var releases = new Releases("com.update.conflict");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        var before = Head(clone);
        File.WriteAllText(Path.Combine(clone, "Runtime", "Code.cs"), "mine\n");
        File.WriteAllText(Path.Combine(clone, "Runtime", "Local.cs"), "class Local {}\n");
        releases.Release("v1.1.0", ("Runtime/Code.cs", "theirs\n"));

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v1.1.0"));

        Assert.Equal(PackageUpdateOutcome.Conflict, result.Outcome);
        Assert.Equal(before, Head(clone));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(clone, "Runtime", "Code.cs")));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Local.cs")));
        Assert.Equal("", GitSandbox.Run(clone, "stash", "list"));
        Assert.Equal("v1.0.0", BasisDevStore.Read(p.Unity, releases.Id)!.Upstream.Ref);
    }

    [GitFact]
    public async Task Update_merges_the_new_release_into_local_commits()
    {
        using var releases = new Releases("com.update.merge");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        GitSandbox.Run(clone, "checkout", "-q", "-b", "studio");
        GitSandbox.Commit(clone, "studio tweak", ("Runtime/Studio.cs", "class Studio {}\n"));
        var commit = releases.Release("v1.1.0", ("Runtime/Added.cs", "class Added {}\n"));

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v1.1.0"));

        Assert.Equal(PackageUpdateOutcome.Updated, result.Outcome);
        Assert.True(result.Merged);
        Assert.Equal("studio", GitSandbox.Run(clone, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal(0, GitSandbox.RunExitCode(clone, "merge-base", "--is-ancestor", commit, "HEAD"));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Studio.cs")));
        Assert.True(File.Exists(Path.Combine(clone, "Runtime", "Added.cs")));
        Assert.Equal(commit, BasisPackagesStore.Find(p.Unity, releases.Id)!.Commit);
    }

    [GitFact]
    public async Task Update_to_a_release_without_a_package_json_is_refused()
    {
        using var releases = new Releases("com.update.nopackage");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        var before = Head(clone);
        releases.Release("v2.0.0", ("package.json", null));

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v2.0.0"));

        Assert.Equal(PackageUpdateOutcome.NotAPackage, result.Outcome);
        Assert.Equal(before, Head(clone));
        Assert.True(File.Exists(Path.Combine(clone, "package.json")));
    }

    [GitFact]
    public async Task A_declined_review_changes_nothing()
    {
        using var releases = new Releases("com.update.review");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        var before = Head(clone);
        var commit = releases.Release("v1.1.0", ("Runtime/Added.cs", "class Added {}\n"));
        PackageUpdateTarget? seen = null;

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("v1.1.0"), (target, _) =>
        {
            seen = target;
            return Task.FromResult(false);
        });

        Assert.Equal(PackageUpdateOutcome.Declined, result.Outcome);
        Assert.Equal(commit, seen!.Commit);
        Assert.Equal("v1.1.0", seen.Ref);
        Assert.Equal(before, Head(clone));
    }

    [GitFact]
    public async Task Check_offers_the_newest_release_that_fits_the_project()
    {
        using var releases = new Releases("com.update.check");
        using var p = new Project();
        releases.Release("v1.1.0", ("package.json", """{ "name": "com.update.check", "version": "1.1.0", "basis": { "from": "2026-01-01" } }"""));
        releases.Release("v1.2.0", ("package.json", """{ "name": "com.update.check", "version": "1.2.0", "unity": "6000.5" }"""));
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var target = ProjectTarget.From("6000.0.30f1", new DateOnly(2026, 6, 1));

        var info = await Updates(p).CheckAsync(p.Install, releases.Id, target, null, null);

        Assert.NotNull(info);
        Assert.True(info!.Available);
        Assert.Equal("v1.0.0", info.InstalledRef);
        Assert.Equal("v1.1.0", info.TargetRef);
        Assert.Equal("v1.2.0", info.SkippedRef);
        Assert.Equal(CompatibilityProblem.NeedsNewerUnity, info.SkippedProblem!.Problem);
    }

    [GitFact]
    public async Task Check_on_the_newest_release_reports_nothing_to_do()
    {
        using var releases = new Releases("com.update.latest");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);

        var info = await Updates(p).CheckAsync(p.Install, releases.Id, ProjectTarget.Unknown, null, null);

        Assert.False(info!.Available);
    }

    [GitFact]
    public async Task Check_on_a_branch_install_reports_new_commits_and_changes_lists_them()
    {
        using var releases = new Releases("com.update.branch");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url)).Ok);
        var tip = GitSandbox.Commit(releases.Source, "new work", ("Runtime/More.cs", "class More {}\n"));
        var updates = Updates(p);

        var info = await updates.CheckAsync(p.Install, releases.Id, ProjectTarget.Unknown, null, null);
        var changes = await updates.GetChangesAsync(p.Install, releases.Id, info!.TargetRef, info.TargetCommit, null);

        Assert.True(info.Available);
        Assert.Equal(PackageUpdateKind.Commits, info.Kind);
        Assert.Equal(tip, info.TargetCommit);
        Assert.Contains(changes.Commits, c => c.Sha == tip && c.Subject == "new work");
    }

    [GitFact]
    public async Task Update_switches_a_clone_to_another_repository_and_keeps_the_old_one_as_upstream()
    {
        using var releases = new Releases("com.update.fork");
        using var p = new Project();
        Assert.True((await p.Mounts.MountAsync(p.Install, releases.Id, releases.Url + "#v1.0.0")).Ok);
        var clone = p.Combine(".basisdev/" + releases.Id);
        var fork = p.Combine("../../fork");
        GitSandbox.Run(Path.GetDirectoryName(fork)!, "clone", "-q", releases.Source, fork);
        GitSandbox.Run(fork, "checkout", "-q", "-b", "team");
        var forked = GitSandbox.Commit(fork, "team change", ("Runtime/Team.cs", "class Team {}\n"));
        var forkUrl = $"https://gitlab.com/basispm-tests/fork-{Guid.NewGuid():N}.git";
        GitSandbox.Run(clone, "config", "url." + fork.Replace('\\', '/') + ".insteadOf", forkUrl);

        var result = await Updates(p).UpdateCloneAsync(p.Install, releases.Id, new PackageUpdateRequest("team", forkUrl, forked, releases.Url, ReplaceForkOf: true));

        Assert.Equal(PackageUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(forked, Head(clone));
        Assert.Equal(forkUrl, GitSandbox.Run(clone, "config", "--get", "remote.origin.url"));
        Assert.Equal(releases.Url, GitSandbox.Run(clone, "config", "--get", "remote.upstream.url"));
        var entry = BasisPackagesStore.Find(p.Unity, releases.Id)!;
        Assert.Equal(forkUrl, entry.Url);
        Assert.Equal("team", entry.Ref);
        Assert.Equal(releases.Url, entry.ForkOf);
    }
}
