using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed partial class BasisDevServiceTests
{
    private sealed class HostedRepository : IDisposable
    {
        private static readonly string[] Names = { "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0" };
        private readonly TempDir _temp = new("hosted");
        private readonly string?[] _previous;

        public HostedRepository(string name, string id, string? subPath)
        {
            Url = $"https://github.com/basispm-tests/{name}-{Guid.NewGuid():N}.git";
            Source = _temp.CreateDir("source");
            GitSandbox.Run(Source, "init", "-q", "-b", "main");
            Project.WritePackage(subPath is null ? Source : Path.Combine(Source, subPath), id, "one");
            GitSandbox.Run(Source, "add", "-A");
            GitSandbox.Run(Source, "commit", "-q", "-m", "package");
            GitSandbox.Run(Source, "tag", "v1");
            Commit = GitSandbox.Run(Source, "rev-parse", "HEAD");
            _previous = Names.Select(Environment.GetEnvironmentVariable).ToArray();
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "url." + Source.Replace('\\', '/') + ".insteadOf");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", Url);
        }

        public string Url { get; }
        public string Source { get; }
        public string Commit { get; }

        public void Dispose()
        {
            for (var i = 0; i < Names.Length; i++) Environment.SetEnvironmentVariable(Names[i], _previous[i]);
            _temp.Dispose();
        }
    }

    [GitFact]
    public async Task Install_clones_a_root_level_package_into_basisdev_and_lists_it()
    {
        using var hosted = new HostedRepository("root", "com.root", null);
        using var p = new Project();

        var result = await p.Mounts.MountAsync(p.Install, "com.root", hosted.Url + "#v1");

        Assert.True(result.Ok, result.Error);
        var clone = p.Combine(".basisdev/com.root");
        Assert.Equal(clone, result.FolderPath);
        Assert.True(File.Exists(Path.Combine(clone, "package.json")));
        Assert.False(Directory.Exists(p.Combine("Packages/com.root")));
        Assert.Equal("file:../.basisdev/com.root", p.Manifest()["com.root"]);
        Assert.Equal(".basisdev/com.root", BasisDevStore.Read(p.Unity, "com.root")!.Folder);
        var entry = BasisPackagesStore.Find(p.Unity, "com.root")!;
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(hosted.Url, entry.Url);
        Assert.Equal("v1", entry.Ref);
        Assert.Null(entry.Path);
        Assert.Equal(hosted.Commit, entry.Commit);
        var package = (await p.Service.ScanAsync(p.Install)).Find("com.root")!;
        Assert.Equal(PackageSourceKind.DevClone, package.Source);
        Assert.True(package.CloneActive);
        Assert.Empty(package.Issues);
        var status = GitSandbox.Status(p.Repo);
        Assert.DoesNotContain(".basisdev", status);
        Assert.Contains("Proj/Packages/basispackages.json", status);

        var removed = await p.Mounts.SwapBackAsync(p.Install, "com.root");

        Assert.True(removed.Ok, removed.Error);
        Assert.False(Directory.Exists(clone));
        Assert.Equal(hosted.Url + "#v1", p.Manifest()["com.root"]);
        Assert.False(File.Exists(BasisPackagesStore.FilePath(p.Unity)));
        Assert.Null(BasisDevStore.Read(p.Unity, "com.root"));
    }

    [GitFact]
    public async Task Install_clones_a_subfolder_package_into_basisdev_and_points_the_manifest_at_it()
    {
        using var hosted = new HostedRepository("sub", "com.sub", "Packages/com.sub");
        using var p = new Project();

        var result = await p.Mounts.MountAsync(p.Install, "com.sub", hosted.Url + "?path=Packages/com.sub#v1");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(p.Combine(".basisdev/com.sub"), result.FolderPath);
        Assert.Equal("file:../.basisdev/com.sub/Packages/com.sub", p.Manifest()["com.sub"]);
        var entry = BasisPackagesStore.Find(p.Unity, "com.sub")!;
        Assert.Equal("Packages/com.sub", entry.Path);
        Assert.Equal("v1", entry.Ref);
        Assert.Empty((await p.Service.ScanAsync(p.Install)).WithIssues);
    }

    [GitFact]
    public async Task Install_refuses_a_package_id_that_is_not_a_folder_name()
    {
        using var p = new Project();

        var result = await p.Mounts.MountAsync(p.Install, "../escape", Upstream + "#v1");

        Assert.False(result.Ok);
        Assert.False(Directory.Exists(p.Combine("escape")));
        Assert.Empty(p.Manifest());
    }

    [GitFact]
    public async Task Fresh_checkout_without_basisdev_offers_to_clone_listed_packages_again()
    {
        using var source = new TempDir("upstream");
        GitSandbox.Run(source.Path, "init", "-q", "-b", "main");
        Project.WritePackage(Path.Combine(source.Path, "Packages", "com.x"), "com.x", "one");
        GitSandbox.Run(source.Path, "add", "-A");
        GitSandbox.Run(source.Path, "commit", "-q", "-m", "package");
        GitSandbox.Run(source.Path, "tag", "v1");
        var commit = GitSandbox.Run(source.Path, "rev-parse", "HEAD");
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisPackagesStore.Set(p.Unity, "com.x", new BasisPackageEntry { Version = "1.0.0", Url = source.Path, Ref = "v1", Path = "Packages/com.x", Commit = commit });
        var listed = File.ReadAllText(BasisPackagesStore.FilePath(p.Unity));

        var missing = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(PackageSourceKind.DevClone, missing.Source);
        Assert.Equal(new[] { BasisDevIssueKind.MissingClone }, missing.Issues);
        Assert.Equal(new[] { BasisDevAction.Restore, BasisDevAction.Reclone }, missing.Actions);
        Assert.Equal(p.Combine(".basisdev/com.x"), p.Mounts.FindMount(p.Unity, "com.x")!.FolderPath);

        var cloned = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Reclone);

        Assert.True(cloned.Ok, cloned.Message);
        Assert.Equal(commit, GitSandbox.Run(p.Combine(".basisdev/com.x"), "rev-parse", "HEAD"));
        var restored = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.True(restored.CloneActive);
        Assert.Empty(restored.Issues);
        Assert.Equal(listed, File.ReadAllText(BasisPackagesStore.FilePath(p.Unity)));
    }

    [GitFact]
    public async Task Restoring_a_listed_package_puts_its_git_line_back_and_unlists_it()
    {
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        BasisPackagesStore.Set(p.Unity, "com.x", new BasisPackageEntry { Url = Upstream, Ref = "v1", Path = "Packages/com.x" });

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Restore);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(Upstream + "?path=Packages/com.x#v1", p.Manifest()["com.x"]);
        Assert.False(File.Exists(BasisPackagesStore.FilePath(p.Unity)));
    }

    [GitFact]
    public async Task Clone_in_use_but_missing_from_basispackages_is_listed_by_reconcile()
    {
        using var p = new Project("""{ "com.x": "file:../.basisdev/com.x/Packages/com.x" }""");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        var head = GitSandbox.Run(clone, "rev-parse", "HEAD");
        BasisDevStore.Write(p.Unity, new BasisDevSidecar
        {
            Package = "com.x",
            Folder = ".basisdev/com.x",
            Upstream = new BasisDevUpstream { Url = Upstream, Ref = "v1", Path = "Packages/com.x", Commit = head },
            Manifest = new BasisDevManifestLine { Original = Upstream + "?path=Packages/com.x#v1", Mounted = "file:../.basisdev/com.x/Packages/com.x" },
        });

        var report = await p.Service.ScanAsync(p.Install);
        var package = report.Find("com.x")!;
        Assert.Equal(new[] { BasisDevIssueKind.Unlisted }, package.Issues);
        Assert.Equal(new[] { BasisDevAction.Record }, package.Actions);
        Assert.Single(report.SafeFixes);

        var result = await p.Service.ReconcileAsync(p.Install);

        Assert.True(result.Ok, result.Message);
        var entry = BasisPackagesStore.Find(p.Unity, "com.x")!;
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(Upstream, entry.Url);
        Assert.Equal("v1", entry.Ref);
        Assert.Equal("Packages/com.x", entry.Path);
        Assert.Equal(head, entry.Commit);
        Assert.Empty((await p.Service.ScanAsync(p.Install)).WithIssues);
    }

    [GitFact]
    public async Task Handing_a_listed_clone_to_the_project_unlists_it()
    {
        using var p = new Project("{}", ("Packages/com.x/package.json", """{ "name": "com.x" }"""));
        p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        BasisPackagesStore.Set(p.Unity, "com.x", new BasisPackageEntry { Url = Upstream, Path = "Packages/com.x" });

        var ignored = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Ignore);

        Assert.True(ignored.Ok, ignored.Message);
        Assert.Null(BasisPackagesStore.Find(p.Unity, "com.x"));
        Assert.True(BasisDevStore.Read(p.Unity, "com.x")!.HandledByProject);
    }

    [GitFact]
    public async Task Clone_an_older_version_left_in_packages_moves_into_basisdev()
    {
        using var p = new Project();
        var clone = p.MakeClone("Packages/com.x", "com.x", null);
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream + "#v1"));
        GitExclude.Add(p.Repo, clone);
        await p.Mounts.RecordAsync(p.Install, "com.x", clone, Upstream + "#v1", null);
        Assert.Null(BasisPackagesStore.Find(p.Unity, "com.x"));

        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Equal(new[] { BasisDevIssueKind.InPackages }, package.Issues);
        Assert.Equal(new[] { BasisDevAction.Move }, package.Actions);

        var moved = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Move);

        Assert.True(moved.Ok, moved.Message);
        var target = p.Combine(".basisdev/com.x");
        Assert.False(Directory.Exists(clone));
        Assert.True(MountService.IsWorkingClone(target));
        Assert.Equal("file:../.basisdev/com.x", p.Manifest()["com.x"]);
        var sidecar = BasisDevStore.Read(p.Unity, "com.x")!;
        Assert.Equal(".basisdev/com.x", sidecar.Folder);
        Assert.Equal(Upstream + "#v1", sidecar.Manifest.Original);
        Assert.Equal("file:../.basisdev/com.x", sidecar.Manifest.Mounted);
        Assert.Equal(target, p.Registry.Find(p.Unity, "com.x")!.FolderPath);
        var entry = BasisPackagesStore.Find(p.Unity, "com.x")!;
        Assert.Equal(Upstream, entry.Url);
        Assert.Equal("v1", entry.Ref);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal(GitSandbox.Run(target, "rev-parse", "HEAD"), entry.Commit);
        var exclude = File.ReadAllText(Path.Combine(p.Repo, ".git", "info", "exclude"));
        Assert.Contains("/Proj/.basisdev/", exclude);
        Assert.DoesNotContain("/Proj/Packages/com.x/", exclude);
        Assert.DoesNotContain(".basisdev", GitSandbox.Status(p.Repo));
        Assert.Empty((await p.Service.ScanAsync(p.Install)).Find("com.x")!.Issues);

        var released = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Release);
        Assert.True(released.Ok, released.Message);
        Assert.Equal(Upstream + "#v1", p.Manifest()["com.x"]);
        Assert.False(File.Exists(BasisPackagesStore.FilePath(p.Unity)));
    }

    [GitFact]
    public async Task Move_points_the_basis_server_link_at_the_new_folder()
    {
        using var p = new Project();
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(p.Repo, "Basis Server", "BasisNetworkCore")).FullName, "BasisNetworkCore.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(p.Repo, "Basis Server", "Directory.Build.targets"), "<Project><Import Project=\"Packages/packages-lock.props\" /></Project>");
        var clone = p.MakeClone("Packages/com.test.transport", "com.test.transport", null);
        GitSandbox.Write(clone, "Network/BasisNetworkCore.asmref", "{ \"reference\": \"BasisNetworkCore\" }");
        GitSandbox.Write(clone, "Network/TestTransport.cs", "public sealed class TestTransport { }");
        p.Registry.Add(new MountRecord(p.Unity, "com.test.transport", clone, Upstream));
        var server = new ServerPackageService(p.Git);
        var linked = await server.InstallLinkedAsync(p.Repo, Upstream, clone, "com.test.transport");
        Assert.True(linked.Ok, linked.Message);

        var moved = await p.Service.ApplyAsync(p.Install, "com.test.transport", BasisDevAction.Move);

        Assert.True(moved.Ok, moved.Message);
        Assert.EndsWith("The Basis Server builds it from .basisdev/com.test.transport now.", moved.Message);
        Assert.Equal(p.Combine(".basisdev/com.test.transport"), server.LoadLinks(p.Repo)["com.test.transport"]);
    }

    [GitFact]
    public async Task Move_leaves_everything_alone_when_basisdev_already_has_that_folder()
    {
        using var p = new Project();
        var clone = p.MakeClone("Packages/com.x", "com.x", null);
        p.Registry.Add(new MountRecord(p.Unity, "com.x", clone, Upstream));
        Project.WritePackage(p.Combine(".basisdev/com.x"), "com.x", "other");

        var result = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Move);

        Assert.False(result.Ok);
        Assert.Contains("already exists", result.Message);
        Assert.True(MountService.IsWorkingClone(clone));
        Assert.Empty(p.Manifest());
    }

    [WindowsFact]
    public async Task Move_changes_nothing_while_a_file_in_the_clone_is_in_use()
    {
        using var t = new TempDir("basisdev-move");
        var (service, install, dll) = FakeClone(t);

        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.ApplyAsync(install, "com.x", BasisDevAction.Move);
            Assert.False(result.Ok);
            Assert.Equal("Couldn't move Packages/com.x: steam_api64.dll is in use by another program, usually Unity. Close it, then try again.", result.Message);
        }

        Assert.True(File.Exists(t.Combine("Proj/Packages/com.x/package.json")));
        Assert.False(Directory.Exists(t.Combine("Proj/.basisdev/com.x")));
        Assert.Equal("https://github.com/o/r.git", (await new UnityProjectService().LoadAsync(install.UnityProjectPath)).Manifest.Dependencies["com.x"]);
    }

    [WindowsFact]
    public async Task A_move_Windows_refuses_puts_the_manifest_line_back()
    {
        using var t = new TempDir("basisdev-move");
        var (service, install, dll) = FakeClone(t);

        using (new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var result = await service.ApplyAsync(install, "com.x", BasisDevAction.Move);
            Assert.False(result.Ok);
            Assert.StartsWith("Couldn't move Packages/com.x to .basisdev/com.x", result.Message);
        }

        Assert.True(File.Exists(t.Combine("Proj/Packages/com.x/package.json")));
        Assert.Equal("https://github.com/o/r.git", (await new UnityProjectService().LoadAsync(install.UnityProjectPath)).Manifest.Dependencies["com.x"]);
    }

    private static (BasisDevService Service, BasisInstall Install, string Dll) FakeClone(TempDir t)
    {
        t.CreateDir("Proj/Assets");
        t.WriteFile("Proj/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
        t.WriteFile("Proj/Packages/manifest.json", """{ "dependencies": { "com.x": "https://github.com/o/r.git" } }""");
        t.WriteFile("Proj/Packages/com.x/package.json", """{ "name": "com.x", "version": "1.0.0" }""");
        var dll = t.WriteFile("Proj/Packages/com.x/Plugins/steam_api64.dll", "x");
        t.CreateDir("Proj/Packages/com.x/.git");
        var project = t.Combine("Proj");
        var registry = new MountRegistry(t.CreateDir("data"));
        registry.Add(new MountRecord(project, "com.x", t.Combine("Proj/Packages/com.x"), "https://github.com/o/r.git"));
        var service = new BasisDevService(new GitService(), new UnityProjectService(), registry, new MountService(new GitService(), new UnityProjectService(), registry));
        return (service, new BasisInstall { RepoRoot = project, UnityProjectPath = project, Name = "Proj", HasUnityProject = true }, dll);
    }
}
