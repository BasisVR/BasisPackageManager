using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class BasisChangesViewModelTests
{
    private const string PrUrl = "https://github.com/BasisVR/Basis/pull/9";

    private static BasisChange Change(string path, BasisChangeKind kind) =>
        new(path, path, kind, kind == BasisChangeKind.Deleted ? null : "100644", kind == BasisChangeKind.Deleted ? null : new string('e', 40));

    private static BasisContributeScan Scan() => new()
    {
        RepoRoot = Path.Combine(Path.GetTempPath(), "basispm-changes-tests"),
        Base = new BasisBaseInfo(new string('a', 40), "developer", BasisBaseSource.History, true, BasisLayout.Whole),
        BaseCommit = new GitCommitInfo(new string('a', 40), "aaaaaaa", "Dev", new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero), "Fix stuff"),
        UnityFolder = "Basis",
        BaseTree = new string('b', 40),
        ProjectTree = new string('d', 40),
        Groups = new[]
        {
            new BasisChangeGroup(BasisChangeArea.Package, "com.basis.a", "Basis/Packages/com.basis.a", true, new[]
            {
                Change("Basis/Packages/com.basis.a/Runtime/A.cs", BasisChangeKind.Modified),
                Change("Basis/Packages/com.basis.a/Runtime/New.meta", BasisChangeKind.Added),
                Change("Basis/Packages/com.basis.a/Runtime/New/B.cs", BasisChangeKind.Added),
                Change("Basis/Packages/com.basis.a/Runtime/New/B.cs.meta", BasisChangeKind.Added),
            }),
            new BasisChangeGroup(BasisChangeArea.Package, "com.mine.c", "Basis/Packages/com.mine.c", false, new[]
            {
                Change("Basis/Packages/com.mine.c/package.json", BasisChangeKind.Added),
            }),
            new BasisChangeGroup(BasisChangeArea.Project, "", "Basis", true, new[]
            {
                Change("Basis/Assets/Scene.unity", BasisChangeKind.Modified),
            }),
        },
    };

    [AvaloniaFact]
    public async Task Load_lists_groups_pairs_meta_files_and_picks_the_basis_packages()
    {
        var host = new FakeHost();
        var vm = new BasisChangesViewModel(host, "Game");

        await vm.LoadAsync();

        Assert.True(vm.HasChanges);
        Assert.Equal(new[] { "com.basis.a", "com.mine.c", L.Tr("basisChanges.group.project") }, vm.Groups.Select(g => g.Name));
        var package = vm.Groups[0];
        Assert.Equal(new[] { "Runtime/A.cs", "Runtime/New/B.cs" }, package.Files.Select(f => f.DisplayPath));
        Assert.True(package.Files[1].HasMeta);
        Assert.True(package.IsChecked);
        Assert.False(vm.Groups[1].IsChecked);
        Assert.False(vm.Groups[2].IsChecked);
        Assert.Equal(4, vm.SelectedCount);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(L.Tr("basisChanges.defaultTitle", "Game"), vm.Title);
        Assert.StartsWith("basispm/game-", vm.Branch);
        Assert.Equal("developer", vm.TargetBranch);
        Assert.Contains("long-term-support-20260916", vm.TargetBranches);
        Assert.Contains("https://gitlab.com/studio/game.git", vm.RemoteText);
        Assert.Contains("BasisVR/Basis", vm.RemoteText);
        Assert.Contains("aaaaaaaaa", vm.BasedOnText);
        Assert.Same(package.Files[0], vm.SelectedFile);
        Assert.True(package.Files[0].IsSelected);
        Assert.Equal(new[] { "@@ -1 +1 @@", "+x    y" }, vm.DiffLines.Select(l => l.Text));
        Assert.True(vm.DiffLines[1].IsAdded);
        Assert.True(vm.CanSubmit);
    }

    [AvaloniaFact]
    public async Task A_focused_package_is_the_only_one_picked()
    {
        var vm = new BasisChangesViewModel(new FakeHost(), "Game", "com.mine.c");

        await vm.LoadAsync();

        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.Groups[1].IsChecked);
        Assert.False(vm.Groups[0].IsChecked);
        Assert.Equal(L.Tr("basisChanges.defaultTitlePackage", "com.mine.c"), vm.Title);
        Assert.True(vm.Groups[1].IsExpanded);
        Assert.False(vm.HasNotice);
    }

    [AvaloniaFact]
    public async Task Group_and_file_checkboxes_drive_the_selection()
    {
        var vm = new BasisChangesViewModel(new FakeHost(), "Game");
        await vm.LoadAsync();

        vm.Groups[0].IsChecked = false;
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.CanSubmit);

        vm.Groups[2].Files[0].IsChecked = true;
        Assert.Equal(1, vm.SelectedCount);
        Assert.True(vm.Groups[2].IsChecked);

        vm.Groups[0].Files[1].IsChecked = true;
        Assert.Null(vm.Groups[0].IsChecked);
        Assert.Equal(4, vm.SelectedCount);
        Assert.Equal(L.Tr("basisChanges.selected", 4), vm.SelectionText);

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(6, vm.SelectedCount);
    }

    [AvaloniaFact]
    public async Task Submit_sends_the_picked_files_and_opens_the_pull_request()
    {
        var host = new FakeHost();
        var vm = new BasisChangesViewModel(host, "Game");
        await vm.LoadAsync();
        vm.Title = "Fix A";
        vm.Body = "  Details  ";

        await vm.SubmitAsync();

        var (paths, draft) = Assert.Single(host.Submitted);
        Assert.Equal(new[]
        {
            "Basis/Packages/com.basis.a/Runtime/A.cs",
            "Basis/Packages/com.basis.a/Runtime/New.meta",
            "Basis/Packages/com.basis.a/Runtime/New/B.cs",
            "Basis/Packages/com.basis.a/Runtime/New/B.cs.meta",
        }, paths);
        Assert.Equal("Fix A", draft.Title);
        Assert.Equal("Details", draft.Body);
        Assert.Equal("developer", draft.TargetBranch);
        Assert.Equal(vm.Branch, draft.Branch);
        Assert.Equal(new[] { PrUrl }, host.Opened);
        Assert.True(vm.HasResult);
        Assert.Contains(PrUrl, vm.ResultText);
        Assert.False(vm.IsSubmitting);
    }

    [AvaloniaFact]
    public async Task Without_a_token_it_asks_once_and_remembers_the_answer()
    {
        var host = new FakeHost { Token = null };
        var vm = new BasisChangesViewModel(host, "Game");
        await vm.LoadAsync();
        var asked = 0;
        vm.AskForToken = () => { asked++; return Task.FromResult<string?>(" pat "); };

        await vm.SubmitAsync();

        Assert.Equal(1, asked);
        Assert.Equal("pat", host.Token);
        Assert.Single(host.Submitted);
    }

    [AvaloniaFact]
    public async Task Cancelling_sign_in_sends_nothing()
    {
        var host = new FakeHost { Token = null };
        var vm = new BasisChangesViewModel(host, "Game");
        await vm.LoadAsync();
        vm.AskForToken = () => Task.FromResult<string?>(null);

        await vm.SubmitAsync();

        Assert.Empty(host.Submitted);
        Assert.Equal(L.Tr("basisChanges.status.signIn"), vm.ErrorText);
    }

    [AvaloniaFact]
    public async Task A_failed_push_offers_the_compare_page()
    {
        var host = new FakeHost { Result = BasisContributeResult.Fail("Pushed it, but no PR", new string('c', 40), "https://github.com/BasisVR/Basis/compare/developer...tester:x?expand=1") };
        var vm = new BasisChangesViewModel(host, "Game");
        await vm.LoadAsync();

        await vm.SubmitAsync();

        Assert.Equal("Pushed it, but no PR", vm.ErrorText);
        Assert.True(vm.HasCompareUrl);
        Assert.Empty(host.Opened);
        vm.OpenCompareCommand.Execute(null);
        Assert.Equal(new[] { "https://github.com/BasisVR/Basis/compare/developer...tester:x?expand=1" }, host.Opened);
    }

    [AvaloniaFact]
    public async Task Blocked_and_empty_scans_show_a_message_instead_of_the_list()
    {
        var blocked = new BasisChangesViewModel(new FakeHost { ScanResult = new BasisContributeScan { Block = BasisContributeBlock.NotGitRepo } }, "Game");
        await blocked.LoadAsync();
        Assert.False(blocked.HasChanges);
        Assert.True(blocked.ShowMessage);
        Assert.Equal(L.Tr("basisChanges.block.notGitRepo"), blocked.MessageText);

        var empty = new BasisChangesViewModel(new FakeHost { ScanResult = Scan() with { Groups = Array.Empty<BasisChangeGroup>() } }, "Game");
        await empty.LoadAsync();
        Assert.False(empty.HasChanges);
        Assert.Equal(L.Tr("basisChanges.empty", "aaaaaaaaa"), empty.MessageText);
    }

    [AvaloniaFact]
    public async Task A_clone_of_basis_and_a_project_without_a_remote_get_their_own_wording()
    {
        var clone = new BasisChangesViewModel(new FakeHost { Remote = "https://github.com/BasisVR/Basis.git" }, "Basis");
        await clone.LoadAsync();
        Assert.Equal(L.Tr("basisChanges.remoteIsBasis", "BasisVR/Basis"), clone.RemoteText);

        var local = new BasisChangesViewModel(new FakeHost { Remote = null }, "Game");
        await local.LoadAsync();
        Assert.Equal(L.Tr("basisChanges.remoteNone", "BasisVR/Basis"), local.RemoteText);
    }

    [AvaloniaFact]
    public async Task Refresh_keeps_the_draft_and_the_selected_file()
    {
        var vm = new BasisChangesViewModel(new FakeHost(), "Game");
        await vm.LoadAsync();
        vm.Title = "Mine";
        vm.SelectFile(vm.Groups[0].Files[1]);

        await vm.LoadAsync();

        Assert.Equal("Mine", vm.Title);
        Assert.Equal("Basis/Packages/com.basis.a/Runtime/New/B.cs", vm.SelectedFile!.Change.Path);
    }

    private sealed class FakeHost : IBasisChangesHost
    {
        public BasisContributeScan ScanResult { get; init; } = Scan();
        public string? Token { get; set; } = "secret";
        public BasisContributeResult Result { get; init; } = BasisContributeResult.Success(PrUrl, new string('c', 40), true, false);
        public List<(IReadOnlyList<string> Paths, BasisPullRequestDraft Draft)> Submitted { get; } = new();
        public List<string> Opened { get; } = new();

        public string Repository => "BasisVR/Basis";
        public Task<BasisContributeScan> ScanAsync(Action<string> progress, CancellationToken ct) => Task.FromResult(ScanResult);
        public string? Remote { get; init; } = "https://gitlab.com/studio/game.git";
        public Task<string?> GetProjectRemoteAsync(string repoRoot) => Task.FromResult(Remote);
        public Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(BasisContributeScan scan, BasisChange change, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<BasisDiffLine>>(new[] { new BasisDiffLine(BasisDiffLineKind.Hunk, "@@ -1 +1 @@"), new BasisDiffLine(BasisDiffLineKind.Added, "+x\ty") });
        public Task<IReadOnlyList<string>> ListBasisBranchesAsync() => Task.FromResult<IReadOnlyList<string>>(new[] { "developer", "long-term-support-20260916" });
        public Task<string?> GetTokenAsync() => Task.FromResult(Token);
        public void RememberToken(string token) => Token = token;
        public Task<GitHubUser?> GetUserAsync(string token) => Task.FromResult<GitHubUser?>(new GitHubUser { Login = "tester", Id = 1 });
        public Task<BasisContributeResult> SubmitAsync(BasisContributeScan scan, IReadOnlyList<string> paths, BasisPullRequestDraft draft, string token, GitHubUser user, Action<string> progress, CancellationToken ct)
        {
            Submitted.Add((paths, draft));
            return Task.FromResult(Result);
        }
        public void OpenUrl(string url) => Opened.Add(url);
    }
}
