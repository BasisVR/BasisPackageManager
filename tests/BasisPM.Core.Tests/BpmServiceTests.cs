using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BpmServiceTests
{
    private sealed class World : IDisposable
    {
        private readonly TempDir _temp = new("bpm");

        public World()
        {
            Upstream = _temp.CreateDir("upstream");
            GitSandbox.Run(Upstream, "init", "-q", "-b", "developer");
            GitSandbox.Run(Upstream, "config", "core.autocrlf", "false");
            First = GitSandbox.Commit(Upstream, "basis",
                ("Basis/Packages/manifest.json", """{ "dependencies": { "com.unity.ugui": "2.0.0", "com.local.thing": "file:../.basisdev/com.local.thing" } }"""),
                ("Basis/Packages/com.basis.a/package.json", """{ "name": "com.basis.a", "version": "1.0.0", "bpmDependencies": { "com.basis.b": "1.0.0" }, "dependencies": { "com.unity.ugui": "2.0.0", "com.unity.render-pipelines.universal": "17.7.0" } }"""),
                ("Basis/Packages/com.unity.render-pipelines.universal/package.json", """{ "name": "com.unity.render-pipelines.universal", "version": "17.7.0" }"""),
                ("Basis/Packages/com.basis.b/package.json", """{ "name": "com.basis.b", "version": "1.0.0", "vpmDependencies": { "com.unity.mathematics": "1.0.0" } }"""),
                ("Basis/Packages/com.basis.c/package.json", """{ "name": "com.basis.c", "version": "1.0.0" }"""),
                ("Basis/Assets/Readme.txt", "one"),
                ("Basis/Assets/Readme.txt.meta", "fileFormatVersion: 2\nguid: 11111111111111111111111111111111\n"),
                ("Basis/Assets/Edited.txt", "base"),
                ("Basis/Assets/Edited.txt.meta", "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\n"),
                ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n"),
                ("Basis/ProjectSettings/ProjectSettings.asset", "PlayerSettings:\n  applicationIdentifier:\n    Android: com.BasisUnity.BasisUnity\n    Standalone: com.Basis-Unity.Basis-Unity\n  bundleVersion: 1\n"),
                ("Basis/ProjectSettings/TagManager.asset", "TagManager:\n  tags:\n  - Grabbable\n  layers:\n  - Default\n  - \n  - Avatar\n"),
                ("Basis Server/readme.txt", "server"));
            Project = _temp.CreateDir("project");
        }

        public string Upstream { get; }
        public string First { get; }
        public string Project { get; }
        public GitService Git { get; } = new(allowLocalRemotes: true);
        public BpmService Bpm => new(Git);
        public string Combine(params string[] parts) => _temp.Combine(parts);
        public string CreateDir(string path) => _temp.CreateDir(path);

        public Dictionary<string, string> Mounts() =>
            System.Text.Json.JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(Project, "Packages", "manifest.json")))!.Dependencies;

        public void Dispose() => _temp.Dispose();
    }

    [GitFact]
    public async Task A_game_project_locks_a_subset_of_basis_with_its_dependencies_and_mounts_it()
    {
        using var w = new World();

        var result = await w.Bpm.InitAsync(w.Project, followsTemplate: false, w.Upstream, "developer", new[] { "com.basis.a" }, copyTemplate: false);

        Assert.True(result.Ok, result.Summary);
        var locked = BpmStore.ReadLock(w.Project);
        Assert.Equal(new[] { "com.basis.a", "com.basis.b", "com.unity.render-pipelines.universal" }, locked.Packages.Keys.Order());
        Assert.Equal("checkout:Basis", locked.Packages["com.unity.render-pipelines.universal"].Version);
        Assert.Equal(0, locked.Packages["com.basis.a"].Depth);
        Assert.Equal(1, locked.Packages["com.basis.b"].Depth);
        Assert.Equal("checkout:Basis", locked.Packages["com.basis.b"].Version);
        Assert.Equal("Basis/Packages/com.basis.b", locked.Packages["com.basis.b"].Folder);
        Assert.Equal(w.First, locked.Checkouts!["Basis"].Commit);
        Assert.Equal(new[] { "com.basis.a" }, BpmStore.ReadManifest(w.Project).Packages.Keys);

        var worktree = Path.Combine(w.Project, ".basisdev", "Basis");
        Assert.True(File.Exists(Path.Combine(worktree, "Basis", "Packages", "com.basis.a", "package.json")));
        Assert.True(File.Exists(Path.Combine(worktree, "Basis", "Packages", "com.basis.b", "package.json")));
        Assert.False(Directory.Exists(Path.Combine(worktree, "Basis", "Packages", "com.basis.c")));
        Assert.False(Directory.Exists(Path.Combine(worktree, "Basis", "Assets")));
        Assert.True(Directory.Exists(Path.Combine(w.Project, ".basisdev", "basis.git")));
        Assert.False(Directory.Exists(Path.Combine(w.Project, "Assets")));

        var mounts = w.Mounts();
        Assert.Equal("file:../.basisdev/Basis/Basis/Packages/com.basis.a", mounts["com.basis.a"]);
        Assert.Equal("file:../.basisdev/Basis/Basis/Packages/com.basis.b", mounts["com.basis.b"]);
        Assert.Equal("2.0.0", mounts["com.unity.ugui"]);
        Assert.False(mounts.ContainsKey("com.local.thing"));

        var status = await w.Bpm.StatusAsync(w.Project);
        Assert.DoesNotContain(status.Issues, i => i.Level != BpmIssueLevel.Info);
    }

    [GitFact]
    public async Task Restore_rebuilds_basisdev_from_the_lock_and_does_nothing_the_second_time()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.a" }, false)).Ok);
        var manifestBefore = File.ReadAllText(Path.Combine(w.Project, "Packages", "manifest.json"));
        FileRetry.DeleteDirectory(Path.Combine(w.Project, ".basisdev"), keepFolder: false);
        GitSandbox.Commit(w.Upstream, "later", ("Basis/Packages/com.basis.a/later.txt", "later"));

        var restored = await w.Bpm.RestoreAsync(w.Project);
        var again = await w.Bpm.RestoreAsync(w.Project);

        Assert.True(restored.Ok, restored.Summary);
        Assert.Contains(restored.Done, d => d.StartsWith("Checked out Basis", StringComparison.Ordinal));
        Assert.True(again.Ok, again.Summary);
        Assert.Contains(again.Done, d => d.Contains("already at", StringComparison.Ordinal));
        var worktree = Path.Combine(w.Project, ".basisdev", "Basis");
        Assert.Equal(w.First, GitSandbox.Run(worktree, "rev-parse", "HEAD"));
        Assert.False(File.Exists(Path.Combine(worktree, "Basis", "Packages", "com.basis.a", "later.txt")));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(w.Project, "Packages", "manifest.json")));
    }

    [GitFact]
    public async Task Restore_stops_when_the_locked_commit_is_gone_from_the_remote()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.c" }, false)).Ok);
        BpmStore.Update(w.Project, (_, locked) => locked.Checkouts!["Basis"].Commit = new string('a', 40));

        var result = await w.Bpm.RestoreAsync(w.Project);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Message.Contains("isn't in", StringComparison.Ordinal));
    }

    [GitFact]
    public async Task A_pinned_checkout_with_edits_is_not_moved()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.c" }, false)).Ok);
        var second = GitSandbox.Commit(w.Upstream, "later", ("Basis/Packages/com.basis.c/later.txt", "later"));
        File.WriteAllText(Path.Combine(w.Project, ".basisdev", "Basis", "Basis", "Packages", "com.basis.c", "package.json"), "{}");

        Assert.True((await w.Bpm.MovePinAsync(w.Project, "Basis", second)).Ok);
        Assert.True((await w.Bpm.ResolveAsync(w.Project)).Ok);
        var result = await w.Bpm.RestoreAsync(w.Project);

        Assert.False(result.Ok);
        Assert.Contains(result.Issues, i => i.Message.Contains("edited file", StringComparison.Ordinal));
        Assert.Equal(w.First, GitSandbox.Run(Path.Combine(w.Project, ".basisdev", "Basis"), "rev-parse", "HEAD"));
    }

    [GitFact]
    public async Task Turning_a_package_off_unmounts_it_and_keeps_the_checkout()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.a", "com.basis.c" }, false)).Ok);

        BpmService.SetEnabled(w.Project, "com.basis.c", false);
        BpmService.SetEnabled(w.Project, "com.basis.a", true);
        Assert.Equal("yes", BpmStore.ReadManifest(w.Project).Packages["com.basis.a"].Enabled);
        Assert.True((await w.Bpm.ResolveAsync(w.Project)).Ok);
        Assert.True((await w.Bpm.RestoreAsync(w.Project)).Ok);
        var mounted = await w.Bpm.MountAsync(w.Project);

        Assert.True(mounted.Ok, mounted.Summary);
        Assert.False(w.Mounts().ContainsKey("com.basis.c"));
        Assert.True(w.Mounts().ContainsKey("com.basis.a"));
        Assert.Equal("no", BpmStore.ReadManifest(w.Project).Packages["com.basis.c"].Enabled);
        Assert.False(BpmStore.ReadLock(w.Project).Packages.ContainsKey("com.basis.c"));
    }

    [GitFact]
    public async Task A_local_override_mounts_another_folder_on_this_machine_only()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.c" }, false)).Ok);
        var mine = w.CreateDir("mine/com.basis.c");
        File.WriteAllText(Path.Combine(mine, "package.json"), """{ "name": "com.basis.c", "version": "9.0.0" }""");
        var lockBefore = File.ReadAllText(BpmStore.LockPath(w.Project));

        w.Bpm.SetOverride(w.Project, "com.basis.c", mine);
        var mounted = await w.Bpm.MountAsync(w.Project);

        Assert.Equal("file:../../mine/com.basis.c", w.Mounts()["com.basis.c"]);
        Assert.Contains(mounted.Issues, i => i.Message.Contains("Don't commit", StringComparison.Ordinal));
        Assert.Equal(lockBefore, File.ReadAllText(BpmStore.LockPath(w.Project)));
        Assert.Contains("com.basis.c", File.ReadAllText(Path.Combine(w.Project, ".basisdev", "bpm-local.json")));

        w.Bpm.SetOverride(w.Project, "com.basis.c", null);
        await w.Bpm.MountAsync(w.Project);
        Assert.Equal("file:../.basisdev/Basis/Basis/Packages/com.basis.c", w.Mounts()["com.basis.c"]);
    }

    [GitFact]
    public async Task A_content_project_copies_the_template_and_updates_it_file_by_file_when_the_pin_moves()
    {
        using var w = new World();
        var init = await w.Bpm.InitAsync(w.Project, followsTemplate: true, w.Upstream, "developer", null, copyTemplate: false);
        Assert.True(init.Ok, init.Summary);
        Assert.Equal("one", File.ReadAllText(Path.Combine(w.Project, "Assets", "Readme.txt")));
        var settings = File.ReadAllText(Path.Combine(w.Project, "ProjectSettings", "ProjectSettings.asset"));
        Assert.Contains("Android: org.BasisUnity.BasisUnity", settings);
        Assert.Contains("Standalone: org.Basis-Unity.Basis-Unity", settings);
        Assert.Equal(new[] { "com.basis.a", "com.basis.b", "com.basis.c", "com.unity.render-pipelines.universal" }, BpmStore.ReadManifest(w.Project).Packages.Keys.Order());
        File.WriteAllText(Path.Combine(w.Project, "Assets", "Edited.txt"), "mine");
        var second = GitSandbox.Commit(w.Upstream, "template",
            ("Basis/Assets/Readme.txt", "two"),
            ("Basis/Assets/Edited.txt", "theirs"),
            ("Basis/Assets/New.txt", "new"),
            ("Basis/Assets/New.txt.meta", "fileFormatVersion: 2\nguid: 33333333333333333333333333333333\n"),
            ("Basis/ProjectSettings/ProjectSettings.asset", "PlayerSettings:\n  applicationIdentifier:\n    Android: com.BasisUnity.BasisUnity\n    Standalone: com.Basis-Unity.Basis-Unity\n  bundleVersion: 2\n"));

        Assert.True((await w.Bpm.MovePinAsync(w.Project, "Basis", second)).Ok);
        var plan = await w.Bpm.PlanTemplateAsync(w.Project, "Basis", second);
        Assert.Equal(BpmTemplateChangeKind.Update, plan.Changes.Single(c => c.Path == "ProjectSettings/ProjectSettings.asset").Kind);

        Assert.Equal(BpmTemplateChangeKind.Update, plan.Changes.Single(c => c.Path == "Assets/Readme.txt").Kind);
        Assert.Equal(BpmTemplateChangeKind.Conflict, plan.Changes.Single(c => c.Path == "Assets/Edited.txt").Kind);
        Assert.Equal(BpmTemplateChangeKind.Add, plan.Changes.Single(c => c.Path == "Assets/New.txt").Kind);

        Assert.True((await w.Bpm.ResolveAsync(w.Project)).Ok);
        Assert.True((await w.Bpm.RestoreAsync(w.Project)).Ok);
        var applied = BpmService.ApplyTemplate(w.Project, plan, Path.Combine(w.Project, ".basisdev", "Basis"), "Basis");

        Assert.True(applied.Ok, applied.Summary);
        Assert.Equal("two", File.ReadAllText(Path.Combine(w.Project, "Assets", "Readme.txt")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(w.Project, "Assets", "Edited.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(w.Project, "Assets", "New.txt")));
        settings = File.ReadAllText(Path.Combine(w.Project, "ProjectSettings", "ProjectSettings.asset"));
        Assert.Contains("bundleVersion: 2", settings);
        Assert.Contains("Android: org.BasisUnity.BasisUnity", settings);
    }

    [GitFact]
    public async Task Embedding_a_package_copies_it_into_packages_records_where_it_came_from_and_drops_its_mount()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.a", "com.basis.c" }, false)).Ok);

        var embedded = BpmService.Embed(w.Project, "com.basis.c");
        Assert.True(embedded.Ok, embedded.Summary);
        var listed = BpmStore.ReadManifest(w.Project).Packages["com.basis.c"];
        Assert.Equal("file:Packages/com.basis.c", listed.Version);
        Assert.Equal(w.Upstream + "?path=Basis/Packages/com.basis.c#" + w.First, listed.CopiedFrom);
        Assert.True(File.Exists(Path.Combine(w.Project, "Packages", "com.basis.c", "package.json")));

        Assert.True((await w.Bpm.ResolveAsync(w.Project)).Ok);
        Assert.True((await w.Bpm.RestoreAsync(w.Project)).Ok);
        var mounted = await w.Bpm.MountAsync(w.Project);

        Assert.True(mounted.Ok, mounted.Summary);
        Assert.False(w.Mounts().ContainsKey("com.basis.c"));
        Assert.True(w.Mounts().ContainsKey("com.basis.a"));
        Assert.False(Directory.Exists(Path.Combine(w.Project, ".basisdev", "Basis", "Basis", "Packages", "com.basis.c")));
        Assert.False(BpmService.Embed(w.Project, "com.basis.c").Ok);
        Assert.Contains((await w.Bpm.StatusAsync(w.Project)).Issues, i => i.Subject == "com.basis.c" && i.Message.Contains(w.First, StringComparison.Ordinal));
    }

    [Fact]
    public void Two_working_trees_of_one_repository_cannot_share_a_branch()
    {
        var manifest = new BpmManifest
        {
            Checkouts = new Dictionary<string, BpmCheckout>
            {
                ["Basis"] = new() { Url = "https://example.com/basis.git", Commit = new string('a', 40) },
                ["ForkA"] = new() { Url = "https://example.com/fork.git", Branch = "mine", Repository = "Basis" },
                ["ForkB"] = new() { Url = "https://example.com/fork.git", Branch = "mine", Repository = "basis" },
                ["ForkC"] = new() { Url = "https://example.com/fork.git", Branch = "other", Repository = "Basis" },
            },
        };

        var issues = BpmService.SharedBranchIssues(manifest);

        Assert.Equal("ForkB", Assert.Single(issues).Subject);
        Assert.Equal(BpmIssueLevel.Error, issues[0].Level);
    }

    [GitFact]
    public async Task Init_adds_basisdev_to_the_projects_gitignore_once()
    {
        using var w = new World();
        GitSandbox.Run(w.Project, "init", "-q");
        File.WriteAllText(Path.Combine(w.Project, ".gitignore"), "Library/");

        var result = await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.c" }, false);

        Assert.True(result.Ok, result.Summary);
        Assert.Equal(new[] { "Library/", ".basisdev/" }, File.ReadAllLines(Path.Combine(w.Project, ".gitignore")));
        Assert.Empty(BpmIgnore.Ensure(Path.Combine(w.Project, ".basisdev")));
        Assert.DoesNotContain((await w.Bpm.StatusAsync(w.Project)).Issues, i => i.Subject == ".basisdev" && i.Level == BpmIssueLevel.Warning);

        File.WriteAllText(Path.Combine(w.Project, ".gitignore"), "Library/\n");
        Assert.Contains((await w.Bpm.StatusAsync(w.Project)).Issues, i => i.Subject == ".basisdev" && i.Level == BpmIssueLevel.Warning);
    }

    [GitFact]
    public async Task A_fork_working_tree_replaces_upstream_packages_and_is_flagged_when_behind_the_pin()
    {
        using var w = new World();
        Assert.True((await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.a", "com.basis.c" }, false)).Ok);
        var fork = w.Combine("fork");
        GitSandbox.Run(w.Combine(), "clone", "-q", w.Upstream, fork);
        GitSandbox.Run(fork, "checkout", "-q", "-b", "my-fork");
        GitSandbox.Commit(fork, "fork change", ("Basis/Packages/com.basis.c/fork.txt", "fork"));
        BpmStore.Update(w.Project, (manifest, _) =>
        {
            manifest.Checkouts!["Fork"] = new BpmCheckout { Url = fork, Branch = "my-fork", Repository = "Basis" };
            manifest.Packages["com.basis.c"].Version = "checkout:Fork";
        });

        var restored = await w.Bpm.RestoreAsync(w.Project);
        Assert.True(restored.Ok, restored.Summary);
        var resolved = await w.Bpm.ResolveAsync(w.Project);
        Assert.True(resolved.Ok, resolved.Summary);
        await w.Bpm.MountAsync(w.Project);

        Assert.Equal("file:../.basisdev/Fork/Basis/Packages/com.basis.c", w.Mounts()["com.basis.c"]);
        Assert.Equal("file:../.basisdev/Basis/Basis/Packages/com.basis.a", w.Mounts()["com.basis.a"]);
        Assert.True(File.Exists(Path.Combine(w.Project, ".basisdev", "Fork", "Basis", "Packages", "com.basis.c", "fork.txt")));
        Assert.Equal("my-fork", BpmStore.ReadLock(w.Project).Checkouts!["Fork"].Branch);
        Assert.DoesNotContain((await w.Bpm.StatusAsync(w.Project)).Issues, i => i.Message.Contains("rebased", StringComparison.Ordinal));

        var later = GitSandbox.Commit(w.Upstream, "upstream moves", ("Basis/Packages/com.basis.a/later.txt", "later"));
        Assert.True((await w.Bpm.MovePinAsync(w.Project, "Basis", later)).Ok);
        Assert.True((await w.Bpm.ResolveAsync(w.Project)).Ok);
        Assert.True((await w.Bpm.RestoreAsync(w.Project)).Ok);

        Assert.Contains((await w.Bpm.StatusAsync(w.Project)).Issues, i => i.Subject == "Fork" && i.Message.Contains("rebased", StringComparison.Ordinal));
    }

    [GitFact]
    public async Task An_existing_project_hears_which_layers_and_tags_basis_expects()
    {
        using var w = new World();
        Directory.CreateDirectory(Path.Combine(w.Project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(w.Project, "ProjectSettings", "TagManager.asset"), "TagManager:\n  tags: []\n  layers:\n  - Default\n");
        Directory.CreateDirectory(Path.Combine(w.Project, "Packages"));
        File.WriteAllText(Path.Combine(w.Project, "Packages", "manifest.json"), """{ "dependencies": { "com.mine": "1.0.0" } }""");

        var result = await w.Bpm.InitAsync(w.Project, false, w.Upstream, "developer", new[] { "com.basis.c" }, false);

        Assert.True(result.Ok, result.Summary);
        Assert.True(result.Issues.Any(i => i.Message.Contains("\"Grabbable\"", StringComparison.Ordinal)), result.Summary);
        Assert.Contains(result.Issues, i => i.Message.Contains("\"Grabbable\"", StringComparison.Ordinal));
        Assert.Equal("1.0.0", w.Mounts()["com.mine"]);
        Assert.False(File.Exists(Path.Combine(w.Project, "ProjectSettings", "ProjectVersion.txt")));
    }

    [Fact]
    public async Task Locking_without_checkouts_counts_embedded_packages_as_provided()
    {
        using var t = new TempDir("bpm");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/Packages/manifest.json", """{ "dependencies": {} }""");
        t.WriteFile("Proj/Packages/com.basis.sdk/package.json", """{ "name": "com.basis.sdk", "version": "0.0.1" }""");
        t.WriteFile("local/com.x/package.json", """{ "name": "com.x", "version": "1.0.0", "bpmDependencies": { "com.basis.sdk": "0.0.1" } }""");
        t.WriteFile("Proj/bpm-manifest.json", """{ "packages": { "com.x": { "version": "file:../local/com.x" } } }""");

        var result = await new BpmService(new GitService(allowLocalRemotes: true)).ResolveAsync(project);

        Assert.True(result.Ok, result.Summary);
        Assert.Empty(result.Issues);
        Assert.Equal("file:../local/com.x", BpmStore.ReadLock(project).Packages["com.x"].Version);
    }

    [Fact]
    public async Task Mount_never_points_a_line_at_a_folder_that_is_not_there()
    {
        using var t = new TempDir("bpm");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/Packages/manifest.json", """{ "dependencies": { "com.x": "https://github.com/o/x.git" } }""");
        BasisPackagesStore.Set(project, "com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Commit = new string('a', 40) });

        var result = await new BpmService(new GitService()).MountAsync(project);

        Assert.False(result.Ok);
        Assert.Contains("\"com.x\": \"https://github.com/o/x.git\"", File.ReadAllText(Path.Combine(project, "Packages", "manifest.json")));
    }

    [Fact]
    public void A_listed_package_without_its_clone_is_missing_even_when_manifest_json_has_the_git_url()
    {
        using var t = new TempDir("bpm");
        var clone = t.CreateDir("clone");
        t.CreateDir("clone/.git");
        t.CreateDir("clone/Basis Server");
        var unity = t.CreateDir("clone/Basis");
        t.CreateDir("clone/Basis/Assets");
        t.WriteFile("clone/Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
        t.WriteFile("clone/Basis/Packages/manifest.json", """{ "dependencies": { "com.x": "https://github.com/o/x.git" } }""");
        BasisPackagesStore.Set(unity, "com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Commit = new string('a', 40) });
        var git = new GitService();
        var registry = new MountRegistry(t.CreateDir("data"));
        var projects = new UnityProjectService();
        var service = new BasisDevService(git, projects, registry, new MountService(git, projects, registry));
        var install = new BasisInstall { RepoRoot = clone, UnityProjectPath = unity, Name = "Basis", Manifest = new PackageManifest { Dependencies = new() { ["com.x"] = "https://github.com/o/x.git" } } };

        var package = service.Scan(install).Find("com.x")!;

        Assert.Contains(BasisDevIssueKind.MissingClone, package.Issues);
        Assert.Contains(BasisDevAction.Reclone, package.Actions);
        Assert.Equal(Path.Combine(clone, ".basisdev", "com.x"), package.CloneFolder);
    }

    [Fact]
    public void Basisdev_is_found_by_walking_up_and_defaults_to_the_clone_root_in_a_basis_clone()
    {
        using var t = new TempDir("locator");
        var clone = t.CreateDir("clone");
        t.CreateDir("clone/.git");
        t.CreateDir("clone/Basis Server");
        var unity = t.CreateDir("clone/Basis");
        var standalone = t.CreateDir("standalone");

        Assert.Equal(Path.Combine(clone, ".basisdev"), BasisDevLocator.Resolve(unity));
        Assert.Equal(Path.Combine(standalone, ".basisdev"), BasisDevLocator.Resolve(standalone));
        var inner = t.CreateDir("clone/Basis/.basisdev");
        Assert.Equal(inner, BasisDevLocator.Resolve(unity));
        var outer = t.CreateDir("deep/.basisdev");
        Assert.Equal(outer, BasisDevLocator.Resolve(t.CreateDir("deep/a/b/project")));
    }

    [Theory]
    [InlineData("1.2.3", BpmVersionKind.Registry, null, null, null)]
    [InlineData("https://github.com/o/r.git#abc1234", BpmVersionKind.Git, "https://github.com/o/r.git", null, "abc1234")]
    [InlineData("https://github.com/o/r.git?path=Packages/x#v1", BpmVersionKind.Git, "https://github.com/o/r.git", "Packages/x", "v1")]
    [InlineData("file:../x", BpmVersionKind.File, null, null, null)]
    [InlineData("checkout:Basis", BpmVersionKind.Checkout, null, null, null)]
    [InlineData("", BpmVersionKind.Invalid, null, null, null)]
    public void Version_forms_parse(string text, BpmVersionKind kind, string? url, string? path, string? gitRef)
    {
        var version = BpmVersion.Parse(text);
        Assert.Equal(kind, version.Kind);
        Assert.Equal(url, version.Url);
        Assert.Equal(path, version.SubPath);
        Assert.Equal(gitRef, version.Ref);
    }
}
