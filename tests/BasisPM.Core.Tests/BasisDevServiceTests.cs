using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed partial class BasisDevServiceTests
{
    private const string Upstream = "https://github.com/example/upstream.git";

    private sealed class Project : IDisposable
    {
        private readonly TempDir _temp = new("basisdev");

        public Project(string dependencies = "{}", params (string Path, string Content)[] tracked)
        {
            Repo = _temp.CreateDir("repo");
            Unity = _temp.CreateDir("repo/Proj");
            _temp.CreateDir("repo/Proj/Assets");
            _temp.WriteFile("repo/Proj/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
            WriteManifest(dependencies);
            foreach (var (path, content) in tracked) _temp.WriteFile("repo/Proj/" + path, content);
            GitSandbox.Run(Repo, "init", "-q", "-b", "main");
            GitSandbox.Run(Repo, "add", "-A");
            GitSandbox.Run(Repo, "commit", "-q", "-m", "project");
            Registry = new MountRegistry(_temp.CreateDir("data"));
            Mounts = new MountService(Git, new UnityProjectService(), Registry);
            Service = new BasisDevService(Git, new UnityProjectService(), Registry, Mounts);
        }

        public string Repo { get; }
        public string Unity { get; }
        public GitService Git { get; } = new(allowLocalRemotes: true);
        public MountRegistry Registry { get; }
        public MountService Mounts { get; }
        public BasisDevService Service { get; }

        public BasisInstall Install => new()
        {
            RepoRoot = Repo,
            UnityProjectPath = Unity,
            Name = "Proj",
            IsGitRepo = true,
            HasUnityProject = true,
        };

        public string Combine(string relative) => _temp.Combine("repo/Proj/" + relative);

        public void WriteManifest(string dependencies) =>
            _temp.WriteFile("repo/Proj/Packages/manifest.json", "{ \"dependencies\": " + dependencies + " }");

        public Dictionary<string, string> Manifest() =>
            JsonDocument.Parse(File.ReadAllText(Combine("Packages/manifest.json"))).RootElement.GetProperty("dependencies")
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        public string MakeClone(string relativeFolder, string id, string? subPath, string? origin = Upstream, string content = "one")
        {
            var source = _temp.CreateDir("sources/" + Guid.NewGuid().ToString("N"));
            GitSandbox.Run(source, "init", "-q", "-b", "main");
            WritePackage(subPath is null ? source : Path.Combine(source, subPath), id, content);
            GitSandbox.Run(source, "add", "-A");
            GitSandbox.Run(source, "commit", "-q", "-m", "package");
            var folder = Combine(relativeFolder);
            var parent = Path.GetDirectoryName(folder)!;
            Directory.CreateDirectory(parent);
            GitSandbox.Run(parent, "clone", "-q", source, folder);
            if (origin is null) GitSandbox.Run(folder, "remote", "remove", "origin");
            else GitSandbox.Run(folder, "remote", "set-url", "origin", origin);
            return folder;
        }

        public static void WritePackage(string root, string id, string content)
        {
            Directory.CreateDirectory(Path.Combine(root, "Runtime"));
            File.WriteAllText(Path.Combine(root, "package.json"), "{ \"name\": \"" + id + "\", \"version\": \"1.0.0\" }\n");
            File.WriteAllText(Path.Combine(root, "Runtime", "Code.cs"), content + "\n");
        }

        public void Dispose() => _temp.Dispose();
    }

    [GitFact]
    public async Task Packages_tracked_by_the_project_repo_and_untracked_folders_are_told_apart()
    {
        using var p = new Project("{}", ("Packages/com.tracked/package.json", """{ "name": "com.tracked" }"""));
        Project.WritePackage(p.Combine("Packages/com.local"), "com.local", "x");

        var report = await p.Service.ScanAsync(p.Install);

        Assert.Equal(PackageSourceKind.ProjectRepo, report.Find("com.tracked")!.Source);
        Assert.Equal(PackageSourceKind.LocalFolder, report.Find("com.local")!.Source);
        Assert.Empty(report.WithIssues);
    }

    [GitFact]
    public async Task Manifest_sources_are_classified()
    {
        using var p = new Project("""
            { "com.git": "https://github.com/o/r.git#v1", "com.ver": "1.2.3", "com.tgz": "file:com.tgz-1.0.0.tgz", "com.path": "file:../../Elsewhere/com.path" }
            """);

        var report = await p.Service.ScanAsync(p.Install);

        Assert.Equal(PackageSourceKind.Git, report.Find("com.git")!.Source);
        Assert.Equal(PackageSourceKind.Registry, report.Find("com.ver")!.Source);
        Assert.Equal(PackageSourceKind.Tarball, report.Find("com.tgz")!.Source);
        Assert.Equal(PackageSourceKind.LocalPath, report.Find("com.path")!.Source);
    }

    [GitFact]
    public async Task Mount_record_writes_a_sidecar_that_makes_an_active_basisdev_clone_healthy()
    {
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        await p.Mounts.RecordAsync(p.Install, "com.x", clone, Upstream + "?path=Packages/com.x#main", "file:../.basisdev/com.x/Packages/com.x");

        var sidecar = BasisDevStore.Read(p.Unity, "com.x");
        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;

        Assert.NotNull(sidecar);
        Assert.Equal(".basisdev/com.x", sidecar!.Folder);
        Assert.Equal(Upstream, sidecar.Upstream.Url);
        Assert.Equal("main", sidecar.Upstream.Ref);
        Assert.Equal("Packages/com.x", sidecar.Upstream.Path);
        Assert.Equal(GitSandbox.Run(clone, "rev-parse", "HEAD"), sidecar.Upstream.Commit);
        Assert.Equal(PackageSourceKind.DevClone, package.Source);
        Assert.True(package.CloneActive);
        Assert.Empty(package.Issues);
        Assert.Contains("/Proj/.basisdev/", File.ReadAllText(Path.Combine(p.Repo, ".git", "info", "exclude")));
        Assert.DoesNotContain(".basisdev", GitSandbox.Status(p.Repo));
    }

    [GitFact]
    public async Task Clone_without_a_sidecar_is_recorded_from_its_origin_and_mount_record()
    {
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream + "?path=Packages/com.x#main"));

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(new[] { BasisDevIssueKind.Unrecorded }, package.Issues);
        Assert.Contains(BasisDevAction.Record, package.Actions);

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Record);

        Assert.True(result.Ok, result.Message);
        var sidecar = BasisDevStore.Read(p.Unity, "com.x")!;
        Assert.Equal(Upstream, sidecar.Upstream.Url);
        Assert.Equal("main", sidecar.Upstream.Ref);
        Assert.Equal("Packages/com.x", sidecar.Upstream.Path);
        Assert.Equal(Upstream + "?path=Packages/com.x#main", sidecar.Manifest.Original);
        Assert.Equal("file:../.basisdev/com.x/Packages/com.x", sidecar.Manifest.Mounted);
        Assert.Empty((await p.Service.ScanAsync(p.Install)).Find("com.x")!.Issues);
    }

    [GitFact]
    public async Task Loose_clone_without_any_record_finds_its_package_by_name()
    {
        using var p = new Project();
        p.MakeClone(".basisdev/com.deep", "com.deep", "Basis/Packages/com.deep", origin: "git@github.com:example/upstream.git");

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.deep")!;

        Assert.Equal(new[] { BasisDevIssueKind.Detached, BasisDevIssueKind.Unrecorded }, package.Issues);
        Assert.Equal(p.Combine(".basisdev/com.deep/Basis/Packages/com.deep"), package.PackageRoot);
        Assert.Equal(Upstream, package.Proposed!.Upstream.Url);
        Assert.Equal("Basis/Packages/com.deep", package.Proposed.Upstream.Path);
    }

    [GitFact]
    public async Task Clone_shadowed_by_an_identical_project_copy_is_reported_as_identical()
    {
        using var p = new Project("{}",
            ("Packages/com.x/package.json", "{ \"name\": \"com.x\", \"version\": \"1.0.0\" }\n"),
            ("Packages/com.x/Runtime/Code.cs", "one\n"));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;

        Assert.Equal(PackageSourceKind.ProjectRepo, package.Source);
        Assert.Contains(BasisDevIssueKind.Shadowed, package.Issues);
        Assert.False(package.CloneActive);
        Assert.True(package.Comparison!.Identical);
        Assert.Equal(GitSandbox.Run(clone, "rev-parse", "HEAD"), package.Comparison.MatchingCommit);
        Assert.Equal(new[] { BasisDevAction.Release, BasisDevAction.Ignore, BasisDevAction.Record }, package.Actions);
    }

    [GitFact]
    public async Task Shadowed_comparison_counts_changes_and_finds_the_matching_upstream_commit()
    {
        using var p = new Project("{}",
            ("Packages/com.x/package.json", "{ \"name\": \"com.x\", \"version\": \"1.0.0\" }\n"),
            ("Packages/com.x/Runtime/Code.cs", "one\n"));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        var first = GitSandbox.Run(clone, "rev-parse", "HEAD");
        GitSandbox.Commit(clone, "upstream moves on", ("Packages/com.x/Runtime/Code.cs", "two\n"), ("Packages/com.x/Runtime/New.cs", "new\n"));

        var comparison = (await p.Service.ScanAsync(p.Install)).Find("com.x")!.Comparison!;

        Assert.False(comparison.Identical);
        Assert.Equal(0, comparison.Added);
        Assert.Equal(1, comparison.Removed);
        Assert.Equal(1, comparison.Modified);
        Assert.Equal(first, comparison.MatchingCommit);

        GitSandbox.Write(p.Repo, "Proj/Packages/com.x/Runtime/Code.cs", "local\n");
        GitSandbox.Write(p.Repo, "Proj/Packages/com.x/Runtime/Mine.cs", "mine\n");
        GitSandbox.Run(p.Repo, "add", "Proj/Packages");
        GitSandbox.Run(p.Repo, "commit", "-q", "-m", "local edit");
        var edited = (await p.Service.ScanAsync(p.Install)).Find("com.x")!.Comparison!;
        Assert.Equal(1, edited.Added);
        Assert.Null(edited.MatchingCommit);
    }

    [GitFact]
    public async Task Release_of_a_shadowed_clone_deletes_only_the_clone()
    {
        using var p = new Project("{}", ("Packages/com.x/package.json", """{ "name": "com.x" }"""));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream + "?path=Packages/com.x#main"));
        await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Record);

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(clone));
        Assert.True(File.Exists(p.Combine("Packages/com.x/package.json")));
        Assert.Null(BasisDevStore.Read(p.Unity, "com.x"));
        Assert.Null(p.Registry.Find(p.Unity, "com.x"));
        Assert.Empty(p.Manifest());
        Assert.Empty((await p.Service.ScanAsync(p.Install)).WithIssues);
    }

    [GitFact]
    public async Task Release_refuses_local_work_until_forced()
    {
        using var p = new Project("{}", ("Packages/com.x/package.json", """{ "name": "com.x" }"""));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        File.WriteAllText(Path.Combine(clone, "Packages", "com.x", "Runtime", "Code.cs"), "edited\n");

        var refused = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);
        Assert.False(refused.Ok);
        Assert.True(refused.NeedsForce);
        Assert.True(Directory.Exists(clone));

        var forced = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release, force: true);
        Assert.True(forced.Ok, forced.Message);
        Assert.False(Directory.Exists(clone));
    }

    [GitFact]
    public async Task Detached_clone_can_be_put_back_in_use_and_released_again()
    {
        const string pinned = "https://github.com/example/upstream.git?path=Packages/com.x#abc1234";
        using var p = new Project("{ \"com.x\": \"" + pinned + "\" }");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream + "?path=Packages/com.x#old"));

        var detached = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(PackageSourceKind.Git, detached.Source);
        Assert.Contains(BasisDevIssueKind.Detached, detached.Issues);
        Assert.Equal(new[] { BasisDevAction.UseClone, BasisDevAction.Release, BasisDevAction.Ignore, BasisDevAction.Record }, detached.Actions);

        var committed = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.UseClone);
        Assert.False(committed.Ok);
        Assert.True(committed.NeedsForce);
        Assert.Equal(pinned, p.Manifest()["com.x"]);

        var used = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.UseClone, force: true);
        Assert.True(used.Ok, used.Message);
        Assert.Equal("file:../.basisdev/com.x/Packages/com.x", p.Manifest()["com.x"]);
        var sidecar = BasisDevStore.Read(p.Unity, "com.x")!;
        Assert.Equal(pinned, sidecar.Manifest.Original);
        Assert.Equal("file:../.basisdev/com.x/Packages/com.x", sidecar.Manifest.Mounted);
        var active = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.True(active.CloneActive);
        Assert.Empty(active.Issues);

        var released = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);
        Assert.True(released.Ok, released.Message);
        Assert.Equal(pinned, p.Manifest()["com.x"]);
        Assert.False(Directory.Exists(clone));
    }

    [GitFact]
    public async Task Use_clone_needs_no_confirmation_when_the_line_is_not_committed()
    {
        using var p = new Project();
        p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        p.WriteManifest("{ \"com.x\": \"" + Upstream + "?path=Packages/com.x#local\" }");

        var used = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.UseClone);

        Assert.True(used.Ok, used.Message);
        Assert.Equal(Upstream + "?path=Packages/com.x#local", BasisDevStore.Read(p.Unity, "com.x")!.Manifest.Original);
    }

    private static void CommitManifestAt(Project p, string dependencies, DateTimeOffset when)
    {
        p.WriteManifest(dependencies);
        var psi = new System.Diagnostics.ProcessStartInfo(new GitService().FindGit()!) { WorkingDirectory = p.Repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "commit", "-q", "-m", "manifest", "--", "Proj/Packages/manifest.json" }) psi.ArgumentList.Add(a);
        var date = "@" + when.ToUnixTimeSeconds() + " +0000";
        psi.Environment["GIT_AUTHOR_DATE"] = date;
        psi.Environment["GIT_COMMITTER_DATE"] = date;
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [GitFact]
    public async Task Release_restores_a_newer_committed_line_instead_of_a_stale_original()
    {
        using var p = new Project();
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        await p.Mounts.RecordAsync(p.Install, "com.x", clone, Upstream + "?path=Packages/com.x#v1", "file:../.basisdev/com.x/Packages/com.x");
        CommitManifestAt(p, "{ \"com.x\": \"" + Upstream + "?path=Packages/com.x#v2\" }", DateTimeOffset.UtcNow.AddDays(1));
        p.WriteManifest("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");

        var released = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);

        Assert.True(released.Ok, released.Message);
        Assert.Equal(Upstream + "?path=Packages/com.x#v2", p.Manifest()["com.x"]);
    }

    [GitFact]
    public async Task Release_keeps_the_recorded_original_when_the_clone_is_newer_than_the_commit()
    {
        using var p = new Project();
        CommitManifestAt(p, "{ \"com.x\": \"" + Upstream + "?path=Packages/com.x#v1\" }", DateTimeOffset.UtcNow.AddDays(-1));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        await p.Mounts.RecordAsync(p.Install, "com.x", clone, Upstream + "?path=Packages/com.x#v2", "file:../.basisdev/com.x/Packages/com.x");
        p.WriteManifest("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");

        var released = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);

        Assert.True(released.Ok, released.Message);
        Assert.Equal(Upstream + "?path=Packages/com.x#v2", p.Manifest()["com.x"]);
    }

    [GitFact]
    public async Task Release_of_an_inactive_clone_leaves_the_manifest_alone()
    {
        const string pinned = "https://github.com/example/upstream.git?path=Packages/com.x#abc1234";
        using var p = new Project("{ \"com.x\": \"" + pinned + "\" }");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);

        Assert.True(result.Ok, result.Message);
        Assert.False(Directory.Exists(clone));
        Assert.Equal(pinned, p.Manifest()["com.x"]);
    }

    [GitFact]
    public async Task Stale_record_on_a_tracked_folder_is_forgotten_without_touching_files()
    {
        using var p = new Project("{}", ("Packages/com.x/package.json", """{ "name": "com.x" }"""));
        p.Registry.Add(new MountRecord(p.Unity, "com.x", p.Combine("Packages/com.x"), "https://github.com/o/r.git"));

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(PackageSourceKind.ProjectRepo, package.Source);
        Assert.Equal(new[] { BasisDevIssueKind.StaleRecord }, package.Issues);

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Prune);

        Assert.True(result.Ok, result.Message);
        Assert.Null(p.Registry.Find(p.Unity, "com.x"));
        Assert.True(File.Exists(p.Combine("Packages/com.x/package.json")));
        Assert.Equal("", GitSandbox.Status(p.Repo));
    }

    [GitFact]
    public async Task Missing_clone_is_restored_to_its_original_manifest_line()
    {
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisDevStore.Write(p.Unity, new BasisDevSidecar
        {
            Package = "com.x",
            Folder = ".basisdev/com.x",
            Upstream = new BasisDevUpstream { Url = Upstream, Ref = "main", Path = "Packages/com.x" },
            Manifest = new BasisDevManifestLine { Original = Upstream + "?path=Packages/com.x#main", Mounted = "file:../.basisdev/com.x/Packages/com.x" },
        });

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(PackageSourceKind.DevClone, package.Source);
        Assert.False(package.CloneExists);
        Assert.Equal(new[] { BasisDevIssueKind.MissingClone }, package.Issues);
        Assert.Equal(new[] { BasisDevAction.Restore, BasisDevAction.Reclone }, package.Actions);

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Restore);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(Upstream + "?path=Packages/com.x#main", p.Manifest()["com.x"]);
        Assert.Null(BasisDevStore.Read(p.Unity, "com.x"));
    }

    [GitFact]
    public async Task Missing_clone_can_be_cloned_again_from_the_recorded_upstream()
    {
        using var source = new TempDir("upstream");
        GitSandbox.Run(source.Path, "init", "-q", "-b", "main");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "one");
        GitSandbox.Run(source.Path, "add", "-A");
        GitSandbox.Run(source.Path, "commit", "-q", "-m", "package");
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisDevStore.Write(p.Unity, new BasisDevSidecar
        {
            Package = "com.x",
            Folder = ".basisdev/com.x",
            Upstream = new BasisDevUpstream { Url = source.Path, Ref = "main", Path = "Packages/com.x" },
            Manifest = new BasisDevManifestLine { Original = Upstream, Mounted = "file:../.basisdev/com.x/Packages/com.x" },
        });

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Reclone);

        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(p.Combine(".basisdev/com.x/Packages/com.x/package.json")));
        Assert.Equal(GitSandbox.Run(source.Path, "rev-parse", "HEAD"), BasisDevStore.Read(p.Unity, "com.x")!.Upstream.Commit);
        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.True(package.CloneActive);
        Assert.Empty(package.Issues);
    }

    [GitFact]
    public async Task Root_level_clone_in_packages_is_an_active_dev_clone_and_release_restores_its_git_line()
    {
        using var p = new Project();
        var clone = p.MakeClone("Packages/com.x", "com.x", null);
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream + "#v1"));
        GitExclude.Add(p.Repo, clone);

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(PackageSourceKind.DevClone, package.Source);
        Assert.True(package.CloneActive);
        Assert.Equal(new[] { BasisDevIssueKind.Unrecorded }, package.Issues);

        Assert.True((await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Record)).Ok);
        Assert.Equal("Packages/com.x", BasisDevStore.Read(p.Unity, "com.x")!.Folder);

        var released = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);
        Assert.True(released.Ok, released.Message);
        Assert.Equal(Upstream + "#v1", p.Manifest()["com.x"]);
        Assert.False(Directory.Exists(clone));
        Assert.Null(BasisDevStore.Read(p.Unity, "com.x"));
    }

    [GitFact]
    public async Task Reconcile_records_clones_and_forgets_stale_records_only()
    {
        using var p = new Project("{}", ("Packages/com.tracked/package.json", """{ "name": "com.tracked" }"""));
        p.Registry.Add(new MountRecord(p.Unity, "com.tracked", p.Combine("Packages/com.tracked"), "https://github.com/o/r.git"));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");

        var result = await p.Service.ReconcileAsync(p.Install);

        Assert.True(result.Ok, result.Message);
        Assert.Null(p.Registry.Find(p.Unity, "com.tracked"));
        Assert.NotNull(BasisDevStore.Read(p.Unity, "com.x"));
        Assert.True(Directory.Exists(clone));
        var remaining = (await p.Service.ScanAsync(p.Install)).WithIssues;
        Assert.Equal(BasisDevIssueKind.Detached, Assert.Single(Assert.Single(remaining).Issues));
    }

    [GitFact]
    public async Task Actions_that_do_not_apply_are_refused()
    {
        using var p = new Project("{}", ("Packages/com.tracked/package.json", """{ "name": "com.tracked" }"""));

        var result = await p.Service.ApplyAsync(p.Install, "com.tracked", BasisDevAction.Release);

        Assert.False(result.Ok);
        Assert.True(File.Exists(p.Combine("Packages/com.tracked/package.json")));
    }

    [GitFact]
    public void Find_mount_prefers_the_sidecar_over_the_machine_registry()
    {
        using var p = new Project();
        p.Registry.Add(new MountRecord(p.Unity, "com.x", p.Combine("Packages/com.x"), "https://github.com/o/old.git"));
        BasisDevStore.Write(p.Unity, new BasisDevSidecar
        {
            Package = "com.x",
            Folder = ".basisdev/com.x",
            Upstream = new BasisDevUpstream { Url = Upstream },
            Manifest = new BasisDevManifestLine { Original = Upstream + "#v2" },
        });

        var mount = p.Mounts.FindMount(p.Unity, "com.x")!;

        Assert.Equal(p.Combine(".basisdev/com.x"), mount.FolderPath);
        Assert.Equal(Upstream + "#v2", mount.OriginalManifestValue);
        Assert.Single(p.Mounts.ListMounts(p.Unity));
    }

    [Fact]
    public void Quick_scan_without_git_still_spots_active_and_inactive_clones()
    {
        using var t = new TempDir();
        t.CreateDir("Proj/Assets");
        t.WriteFile("Proj/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
        t.WriteFile("Proj/Packages/manifest.json", """{ "dependencies": { "com.a": "file:../.basisdev/com.a" } }""");
        t.CreateDir("Proj/.basisdev/com.a/.git");
        t.WriteFile("Proj/.basisdev/com.a/package.json", """{ "name": "com.a" }""");
        t.CreateDir("Proj/.basisdev/com.b/.git");
        t.WriteFile("Proj/.basisdev/com.b/package.json", """{ "name": "com.b" }""");
        var registry = new MountRegistry(t.CreateDir("data"));
        var service = new BasisDevService(new GitService(), new UnityProjectService(), registry, new MountService(new GitService(), new UnityProjectService(), registry));
        var install = new BasisInstall { RepoRoot = t.Combine("Proj"), UnityProjectPath = t.Combine("Proj"), Name = "Proj", HasUnityProject = true };
        install.Manifest.Dependencies["com.a"] = "file:../.basisdev/com.a";

        var report = service.Scan(install);

        Assert.True(report.Find("com.a")!.CloneActive);
        Assert.Equal(PackageSourceKind.DevClone, report.Find("com.a")!.Source);
        Assert.False(report.Find("com.b")!.CloneActive);
        Assert.Contains(BasisDevIssueKind.Detached, report.Find("com.b")!.Issues);
    }
}
