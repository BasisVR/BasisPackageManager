using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisContributeServiceTests
{
    private const string PrUrl = "https://github.com/BasisVR/Basis/pull/7";

    private static readonly GitHubUser Tester = new() { Login = "tester", Id = 42, Name = "Test Er" };

    private static string SeedBasis(GitSandbox box) => box.CommitUpstream("basis",
        ("README.md", "basis\n"),
        ("Basis/Assets/Scene.unity", "scene\n"),
        ("Basis/ProjectSettings/ProjectSettings.asset", "settings\n"),
        ("Basis/Packages/manifest.json", "{}\n"),
        ("Basis/Packages/com.basis.a/package.json", "{\"name\":\"com.basis.a\"}\n"),
        ("Basis/Packages/com.basis.a/Runtime/A.cs", "class A {}\n"),
        ("Basis/Packages/com.basis.a/Runtime/A.cs.meta", "guid: a\n"),
        ("Basis/Packages/com.basis.a/Runtime/Old.cs", "class Old {}\n"),
        ("Basis/Packages/com.basis.a/Runtime/Old.cs.meta", "guid: old\n"),
        ("Basis/Packages/com.basis.b/package.json", "{\"name\":\"com.basis.b\"}\n"));

    private static BasisContributeService Service(GitSandbox box, HttpClient? http = null, string? pushTarget = null) =>
        new(box.Git, box.Updates, new GitHubApiService(http ?? StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound)), "BasisVR/Basis",
            (_, _, _) => pushTarget ?? throw new InvalidOperationException("no push target"));

    private static void EditProject(string project)
    {
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/A.cs", "class A { int x; }\n");
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/Old.cs", null);
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/Old.cs.meta", null);
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/New/B.cs", "class B {}\n");
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/New/B.cs.meta", "guid: b\n");
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/New.meta", "guid: folder\n");
        GitSandbox.Commit(project, "my scene", ("Basis/Assets/Scene.unity", "my scene\n"));
        GitSandbox.Write(project, "Basis/Packages/com.mine.c/package.json", "{\"name\":\"com.mine.c\"}\n");
        GitSandbox.Write(project, "notes.txt", "mine\n");
    }

    private static HashSet<string> TreeFiles(string repo, string commit) =>
        GitSandbox.Run(repo, "ls-tree", "-r", "--name-only", commit).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    [GitFact]
    public async Task Scan_groups_changes_since_the_basis_base_without_touching_the_working_copy()
    {
        using var box = new GitSandbox();
        var basis = SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var head = GitSandbox.Run(project, "rev-parse", "HEAD");
        var status = GitSandbox.Status(project);
        var index = File.ReadAllBytes(Path.Combine(project, ".git", "index"));

        var scan = await Service(box).ScanAsync(project, Path.Combine(project, "Basis"));

        Assert.Equal(BasisContributeBlock.None, scan.Block);
        Assert.Equal(basis, scan.Base!.Sha);
        Assert.Equal("Basis", scan.UnityFolder);
        Assert.Equal(new[] { "com.basis.a", "com.mine.c", "", "" }, scan.Groups.Select(g => g.Name));
        Assert.Equal(new[] { BasisChangeArea.Package, BasisChangeArea.Package, BasisChangeArea.Project, BasisChangeArea.Repository }, scan.Groups.Select(g => g.Area));
        var package = scan.FindPackage("com.basis.a")!;
        Assert.True(package.InBasis);
        Assert.False(scan.FindPackage("com.mine.c")!.InBasis);
        Assert.Equal(new[]
        {
            ("Runtime/A.cs", BasisChangeKind.Modified),
            ("Runtime/New.meta", BasisChangeKind.Added),
            ("Runtime/New/B.cs", BasisChangeKind.Added),
            ("Runtime/New/B.cs.meta", BasisChangeKind.Added),
            ("Runtime/Old.cs", BasisChangeKind.Deleted),
            ("Runtime/Old.cs.meta", BasisChangeKind.Deleted),
        }, package.Changes.Select(c => (package.RelativePath(c), c.Kind)));
        Assert.Equal("Basis/Assets/Scene.unity", Assert.Single(scan.Groups[2].Changes).Path);
        Assert.Equal("notes.txt", Assert.Single(scan.Groups[3].Changes).Path);

        Assert.Equal(index, File.ReadAllBytes(Path.Combine(project, ".git", "index")));
        Assert.Equal(head, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal(status, GitSandbox.Status(project));
        Assert.Empty(Directory.GetFiles(Path.Combine(project, ".git"), "basispm-*.index*"));
    }

    [GitFact]
    public async Task Commit_holds_only_the_selected_package_on_top_of_the_basis_base()
    {
        using var box = new GitSandbox();
        var basis = SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var service = Service(box);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        var commit = await service.CreateCommitAsync(scan, BasisContributeService.SuggestSelection(scan), "Fix A", "Test Er", "42+tester@users.noreply.github.com");

        Assert.True(commit.Ok, commit.Error);
        Assert.Equal(6, commit.FileCount);
        Assert.Equal(basis, GitSandbox.Run(project, "rev-parse", commit.Commit + "^"));
        Assert.Equal("Fix A", GitSandbox.Run(project, "log", "-1", "--format=%B", commit.Commit!));
        Assert.Equal("Test Er <42+tester@users.noreply.github.com>", GitSandbox.Run(project, "log", "-1", "--format=%an <%ae>", commit.Commit!));
        var changed = GitSandbox.Run(project, "diff", "--name-status", basis, commit.Commit!).Split('\n').Select(l => l.Replace('\t', ' ')).ToList();
        Assert.Equal(new[]
        {
            "M Basis/Packages/com.basis.a/Runtime/A.cs",
            "A Basis/Packages/com.basis.a/Runtime/New.meta",
            "A Basis/Packages/com.basis.a/Runtime/New/B.cs",
            "A Basis/Packages/com.basis.a/Runtime/New/B.cs.meta",
            "D Basis/Packages/com.basis.a/Runtime/Old.cs",
            "D Basis/Packages/com.basis.a/Runtime/Old.cs.meta",
        }, changed);
        Assert.Equal("class A { int x; }", GitSandbox.Run(project, "show", commit.Commit + ":Basis/Packages/com.basis.a/Runtime/A.cs"));
        Assert.Equal("scene", GitSandbox.Run(project, "show", commit.Commit + ":Basis/Assets/Scene.unity"));
        Assert.Equal("my scene", GitSandbox.Read(project, "Basis/Assets/Scene.unity").TrimEnd());
    }

    [GitFact]
    public async Task Picking_one_file_brings_its_meta_and_new_folder_meta()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var scan = await Service(box).ScanAsync(project, Path.Combine(project, "Basis"));

        var picked = BasisContributeService.ExpandSelection(scan, new[] { "Basis/Packages/com.basis.a/Runtime/New/B.cs", "Basis/Packages/com.basis.a/Runtime/Old.cs.meta" });

        Assert.Equal(new[]
        {
            "Basis/Packages/com.basis.a/Runtime/New.meta",
            "Basis/Packages/com.basis.a/Runtime/New/B.cs",
            "Basis/Packages/com.basis.a/Runtime/New/B.cs.meta",
            "Basis/Packages/com.basis.a/Runtime/Old.cs",
            "Basis/Packages/com.basis.a/Runtime/Old.cs.meta",
        }, picked.Select(c => c.Path));
    }

    [Fact]
    public void Deleted_folder_meta_follows_only_when_everything_inside_goes()
    {
        var scan = new BasisContributeScan
        {
            Groups = new[]
            {
                new BasisChangeGroup(BasisChangeArea.Project, "", "", true, new[]
                {
                    new BasisChange("Assets/Gone.meta", "Assets/Gone.meta", BasisChangeKind.Deleted, null, null),
                    new BasisChange("Assets/Gone/One.cs", "Assets/Gone/One.cs", BasisChangeKind.Deleted, null, null),
                    new BasisChange("Assets/Gone/Two.cs", "Assets/Gone/Two.cs", BasisChangeKind.Deleted, null, null),
                }),
            },
        };

        Assert.DoesNotContain(BasisContributeService.ExpandSelection(scan, new[] { "Assets/Gone/One.cs" }), c => c.Path == "Assets/Gone.meta");
        Assert.Contains(BasisContributeService.ExpandSelection(scan, new[] { "Assets/Gone/One.cs", "Assets/Gone/Two.cs" }), c => c.Path == "Assets/Gone.meta");
    }

    [GitFact]
    public async Task Rehosted_unity_folder_copy_maps_changes_back_into_the_basis_folder()
    {
        using var box = new GitSandbox();
        var basis = SeedBasis(box);
        var project = box.CopyProject(basis, "copy", BasisUpdateService.BasisUnityFolder);
        GitSandbox.Run(project, "commit", "-q", "--allow-empty", "-m", $"Record Basis\n\nBasis-Commit: {basis}\nBasis-Branch: developer\nBasis-Folder: Basis -> .");
        GitSandbox.Write(project, "Packages/com.basis.a/Runtime/A.cs", "class A { int y; }\n");
        GitSandbox.Write(project, "Assets/MyGame.unity", "mine\n");
        var service = Service(box);

        var scan = await service.ScanAsync(project, project);

        Assert.Equal(BasisContributeBlock.None, scan.Block);
        Assert.Equal(BasisBaseSource.Recorded, scan.Base!.Source);
        Assert.Equal("Basis", scan.UnityFolder);
        var change = Assert.Single(scan.FindPackage("com.basis.a")!.Changes);
        Assert.Equal("Basis/Packages/com.basis.a/Runtime/A.cs", change.Path);
        Assert.Equal("Packages/com.basis.a/Runtime/A.cs", change.ProjectPath);
        var commit = await service.CreateCommitAsync(scan, new[] { change.Path }, "Fix A", "Test Er", "t@example.com");
        Assert.True(commit.Ok, commit.Error);
        Assert.Equal(basis, GitSandbox.Run(project, "rev-parse", commit.Commit + "^"));
        Assert.Equal(TreeFiles(box.Upstream, basis), TreeFiles(project, commit.Commit!));
        Assert.Equal("class A { int y; }", GitSandbox.Run(project, "show", commit.Commit + ":Basis/Packages/com.basis.a/Runtime/A.cs"));
    }

    [GitFact]
    public async Task Submit_forks_pushes_the_commit_and_opens_a_pull_request()
    {
        using var box = new GitSandbox();
        var basis = SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var fork = box.Combine("fork.git");
        GitSandbox.Run(box.Combine(), "clone", "-q", "--bare", box.Upstream, fork);
        var bodies = new Dictionary<string, string>();
        var handler = GitHub(forkedPrs: 0, bodies: bodies);
        var service = Service(box, new HttpClient(handler), fork);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        var result = await service.SubmitAsync(scan, BasisContributeService.SuggestSelection(scan, "com.basis.a"),
            new BasisPullRequestDraft("Fix A", "Details", "basispm/fix-a", "developer"), "secret", Tester);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(PrUrl, result.Url);
        Assert.True(result.Forked);
        Assert.False(result.Updated);
        Assert.Equal(result.Commit, GitSandbox.Run(fork, "rev-parse", "refs/heads/basispm/fix-a"));
        Assert.Equal(basis, GitSandbox.Run(fork, "rev-parse", "refs/heads/basispm/fix-a^"));
        Assert.Equal(result.Commit, GitSandbox.Run(project, "rev-parse", BasisContributeService.RefPrefix + "basispm/fix-a"));
        var body = bodies["POST /repos/BasisVR/Basis/pulls"];
        Assert.Contains("\"head\":\"tester:basispm/fix-a\"", body);
        Assert.Contains("\"base\":\"developer\"", body);
        Assert.Contains("Based on Basis", body);
        Assert.Contains(handler.Requests, r => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/merge-upstream"));
    }

    [GitFact]
    public async Task Submitting_the_same_branch_again_updates_the_open_pull_request()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var fork = box.Combine("fork.git");
        GitSandbox.Run(box.Combine(), "clone", "-q", "--bare", box.Upstream, fork);
        var draft = new BasisPullRequestDraft("Fix A", null, "basispm/fix-a", "developer");
        var first = Service(box, new HttpClient(GitHub(forkedPrs: 0)), fork);
        var scan = await first.ScanAsync(project, Path.Combine(project, "Basis"));
        Assert.True((await first.SubmitAsync(scan, new[] { "Basis/Packages/com.basis.a/Runtime/A.cs" }, draft, "secret", Tester)).Ok);

        var second = Service(box, new HttpClient(GitHub(forkedPrs: 1)), fork);
        var result = await second.SubmitAsync(scan, BasisContributeService.SuggestSelection(scan), draft, "secret", Tester);

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Updated);
        Assert.Equal(result.Commit, GitSandbox.Run(fork, "rev-parse", "refs/heads/basispm/fix-a"));
        Assert.Equal(6, GitSandbox.Run(fork, "diff", "--name-only", "refs/heads/basispm/fix-a^", "refs/heads/basispm/fix-a").Split('\n').Length);
    }

    [GitFact]
    public async Task Someone_elses_branch_with_the_same_name_is_left_alone()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var fork = box.Combine("fork.git");
        GitSandbox.Run(box.Combine(), "clone", "-q", "--bare", box.Upstream, fork);
        GitSandbox.Run(fork, "branch", "taken", "developer");
        var service = Service(box, new HttpClient(GitHub(forkedPrs: 0)), fork);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        var result = await service.SubmitAsync(scan, BasisContributeService.SuggestSelection(scan), new BasisPullRequestDraft("Fix A", null, "taken", "developer"), "secret", Tester);

        Assert.False(result.Ok);
        Assert.Contains("already has a branch named taken", result.Error);
        Assert.Equal(GitSandbox.Run(fork, "rev-parse", "developer"), GitSandbox.Run(fork, "rev-parse", "taken"));
    }

    [GitFact]
    public async Task Maintainers_push_straight_to_basis()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        var upstreamCopy = box.Combine("basis.git");
        GitSandbox.Run(box.Combine(), "clone", "-q", "--bare", box.Upstream, upstreamCopy);
        var bodies = new Dictionary<string, string>();
        var handler = GitHub(forkedPrs: 0, canPush: true, bodies: bodies);
        var service = Service(box, new HttpClient(handler), upstreamCopy);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        var result = await service.SubmitAsync(scan, BasisContributeService.SuggestSelection(scan), new BasisPullRequestDraft("Fix A", null, "fix-a", "developer"), "secret", Tester);

        Assert.True(result.Ok, result.Error);
        Assert.False(result.Forked);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/forks"));
        Assert.Contains("\"head\":\"fix-a\"", bodies["POST /repos/BasisVR/Basis/pulls"]);
    }

    [GitFact]
    public async Task Scan_refuses_while_a_merge_is_unfinished()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        File.WriteAllText(Path.Combine(project, ".git", "MERGE_HEAD"), GitSandbox.Run(project, "rev-parse", "HEAD") + "\n");

        var scan = await Service(box).ScanAsync(project, Path.Combine(project, "Basis"));

        Assert.Equal(BasisContributeBlock.OperationInProgress, scan.Block);
    }

    [GitFact]
    public async Task Scan_refuses_a_folder_that_is_not_in_git()
    {
        using var box = new GitSandbox();
        var folder = box.Combine("loose");
        Directory.CreateDirectory(folder);

        Assert.Equal(BasisContributeBlock.NotGitRepo, (await Service(box).ScanAsync(folder, folder)).Block);
    }

    [GitFact]
    public async Task Nothing_selected_or_nothing_different_makes_no_commit()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        var service = Service(box);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        Assert.Equal(0, scan.ChangeCount);
        Assert.False((await service.CreateCommitAsync(scan, Array.Empty<string>(), "x", "a", "a@b.c")).Ok);
    }

    [GitFact]
    public async Task Diff_shows_changed_new_and_binary_files()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        EditProject(project);
        File.WriteAllBytes(Path.Combine(project, "Basis", "Assets", "Icon.png"), new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 1, 2, 3 });
        var service = Service(box);
        var scan = await service.ScanAsync(project, Path.Combine(project, "Basis"));

        var modified = await service.GetDiffAsync(scan, scan.Changes.Single(c => c.Path.EndsWith("Runtime/A.cs")));
        Assert.Contains(modified, l => l.Kind == BasisDiffLineKind.Hunk);
        Assert.Contains(modified, l => l.Kind == BasisDiffLineKind.Removed && l.Text == "-class A {}");
        Assert.Contains(modified, l => l.Kind == BasisDiffLineKind.Added && l.Text == "+class A { int x; }");
        Assert.Contains(await service.GetDiffAsync(scan, scan.Changes.Single(c => c.Path.EndsWith("New/B.cs"))), l => l.Kind == BasisDiffLineKind.Added && l.Text == "+class B {}");
        Assert.Contains(await service.GetDiffAsync(scan, scan.Changes.Single(c => c.Path.EndsWith("Runtime/Old.cs"))), l => l.Kind == BasisDiffLineKind.Removed && l.Text == "-class Old {}");
        Assert.Contains(await service.GetDiffAsync(scan, scan.Changes.Single(c => c.Path.EndsWith("Icon.png"))), l => l.Kind == BasisDiffLineKind.Note && l.Text.StartsWith("Binary files"));
    }

    [Fact]
    public void Long_diffs_stop_with_a_note()
    {
        var lines = BasisContributeService.ParseDiff("diff --git a/x b/x\nnew file mode 100644\nindex 0000000..1111111\n--- /dev/null\n+++ b/x\n@@ -0,0 +1,4 @@\n+a\n+b\n+c\n+d\n", 4);

        Assert.Equal(new[] { BasisDiffLineKind.Header, BasisDiffLineKind.Hunk, BasisDiffLineKind.Added, BasisDiffLineKind.Added, BasisDiffLineKind.Note },
            lines.Select(l => l.Kind));
        Assert.Equal("new file mode 100644", lines[0].Text);
        Assert.Equal("… 2 more lines not shown.", lines[^1].Text);
    }

    [Fact]
    public void Huge_lines_are_cut_so_the_viewer_stays_fast()
    {
        var lines = BasisContributeService.ParseDiff("@@ -1 +1 @@\n+" + new string('f', 2_097_169) + "\n");

        Assert.Equal(2, lines.Count);
        Assert.StartsWith("+fff", lines[1].Text);
        Assert.EndsWith(" … 2095170 more characters", lines[1].Text);
        Assert.True(lines[1].Text.Length < 2100);
    }

    [Fact]
    public void Editor_churn_starts_unpicked()
    {
        var sha = new string('e', 40);
        BasisChange Modified(string path) => new(path, path, BasisChangeKind.Modified, "100644", sha);
        var scan = new BasisContributeScan
        {
            UnityFolder = "Basis",
            Groups = new[]
            {
                new BasisChangeGroup(BasisChangeArea.Package, "com.basis.sdk", "Basis/Packages/com.basis.sdk", true, new[]
                {
                    Modified("Basis/Packages/com.basis.sdk/Editor/Build.cs"),
                    Modified("Basis/Packages/com.basis.sdk/Fonts/Inter SDF.asset"),
                    new BasisChange("Basis/Packages/com.basis.sdk/Fonts/New SDF.asset", "x", BasisChangeKind.Added, "100644", sha),
                }),
            },
        };

        Assert.Equal(new[] { "Basis/Packages/com.basis.sdk/Editor/Build.cs", "Basis/Packages/com.basis.sdk/Fonts/New SDF.asset" }, BasisContributeService.SuggestSelection(scan));
        Assert.True(BasisContributeService.IsEditorChurn(scan, Modified("Basis/Packages/packages-lock.json")));
        Assert.True(BasisContributeService.IsEditorChurn(scan, Modified("Basis/ProjectSettings/ProjectVersion.txt")));
        Assert.False(BasisContributeService.IsEditorChurn(scan, Modified("Basis/ProjectSettings/QualitySettings.asset")));
    }

    [Fact]
    public void Branch_and_target_suggestions()
    {
        Assert.Equal("basispm/com.basis.framework-20261010-0930", BasisContributeService.SuggestBranch("com.basis.framework", new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero)));
        Assert.Equal("basispm/my-game-20261010-0930", BasisContributeService.SuggestBranch("My Game!", new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero)));
        Assert.Equal("basispm/changes-20261010-0930", BasisContributeService.SuggestBranch("  ", new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero)));
        var sha = new string('a', 40);
        Assert.Equal("developer", BasisContributeService.SuggestTarget(new BasisContributeScan { Base = new BasisBaseInfo(sha, "long-term-support-20260916", BasisBaseSource.History, true, BasisLayout.Whole) }));
        Assert.Equal("experimental", BasisContributeService.SuggestTarget(new BasisContributeScan { Base = new BasisBaseInfo(sha, "experimental", BasisBaseSource.History, true, BasisLayout.Whole) }));
    }

    private static StubHttpMessageHandler GitHub(int forkedPrs, bool canPush = false, Dictionary<string, string>? bodies = null)
    {
        var openPulls = forkedPrs == 0 ? "[]" : $"[{{\"html_url\":\"{PrUrl}\",\"number\":7,\"state\":\"open\"}}]";
        return new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (bodies is not null && request.Content is not null) bodies[$"{request.Method.Method} {path}"] = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var json = (request.Method.Method, path) switch
            {
                ("GET", "/repos/BasisVR/Basis") => $"{{\"name\":\"Basis\",\"full_name\":\"BasisVR/Basis\",\"default_branch\":\"developer\",\"permissions\":{{\"push\":{(canPush ? "true" : "false")}}}}}",
                ("POST", "/repos/BasisVR/Basis/forks") => "{\"name\":\"Basis\",\"full_name\":\"tester/Basis\",\"owner\":{\"login\":\"tester\",\"id\":42}}",
                ("GET", "/repos/tester/Basis") => "{\"name\":\"Basis\",\"full_name\":\"tester/Basis\"}",
                ("POST", "/repos/tester/Basis/merge-upstream") => "{\"merge_type\":\"none\"}",
                ("GET", "/repos/BasisVR/Basis/pulls") => openPulls,
                ("POST", "/repos/BasisVR/Basis/pulls") => $"{{\"html_url\":\"{PrUrl}\",\"number\":7,\"state\":\"open\"}}",
                _ => null,
            };
            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : StubHttpMessageHandler.Json(request.Method == HttpMethod.Post && path.EndsWith("/pulls") ? HttpStatusCode.Created : HttpStatusCode.OK, json);
        });
    }
}
