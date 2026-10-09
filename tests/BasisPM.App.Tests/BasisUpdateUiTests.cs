using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class BasisUpdateUiTests
{
    private static readonly IReadOnlyList<GitCommitInfo> Commits = new[]
    {
        new GitCommitInfo("a1", "a1a1a1a1a", "Tester", DateTimeOffset.Now, "Newest change"),
        new GitCommitInfo("b2", "b2b2b2b2b", "Tester", DateTimeOffset.Now.AddDays(-1), "Older change"),
    };

    private static BasisUpdatePlan Plan(BasisUpdateKind kind, string branch = "developer") => new()
    {
        Kind = kind,
        BasisBranch = branch,
        LocalBranch = "my-game",
        HeadSha = "head",
        UpstreamSha = "upstream",
        MergeBase = "base",
        IncomingCount = 70,
        IncomingCommits = Commits,
    };

    private static BasisUpdateReviewViewModel Review(BasisUpdatePlan plan, Func<string, Task<BasisUpdatePlan?>>? replan = null) =>
        new("My Game", plan, unityOpen: false,
            replan ?? (_ => Task.FromResult<BasisUpdatePlan?>(null)),
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "developer", "long-term-support-20260916" }));

    private static InstallRow Row(string name) => new(new BasisInstall
    {
        RepoRoot = Path.Combine(Path.GetTempPath(), name),
        UnityProjectPath = Path.Combine(Path.GetTempPath(), name),
        Name = name,
        HasUnityProject = true,
        IsGitRepo = true,
        IsBasisCheckout = true,
    });

    [AvaloniaFact]
    public void Project_card_reflects_each_basis_check_state()
    {
        Localizer.Instance.SetLanguage("en");
        var row = Row("Game");
        Assert.False(row.HighlightBasisUpdate);

        row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "developer", "tip", 70, false));
        Assert.True(row.BasisUpdateAvailable);
        Assert.True(row.HighlightBasisUpdate);
        Assert.False(row.BasisPillMuted);
        Assert.Contains("70", row.BasisPillText);
        Assert.Equal("Update Basis", row.UpdateButtonLabel);

        row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "developer", "tip", null, true));
        Assert.True(row.BasisUpdateInProgress);
        Assert.False(row.BasisUpdateAvailable);
        Assert.Equal("Finish update", row.UpdateButtonLabel);

        row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpToDate, "long-term-support-20260916", "tip", 0, false));
        Assert.True(row.BasisPillMuted);
        Assert.False(row.HighlightBasisUpdate);
        Assert.Contains("long-term-support-20260916", row.BasisPillText);
    }

    [AvaloniaFact]
    public void Review_offers_the_right_action_for_each_kind_of_plan()
    {
        Localizer.Instance.SetLanguage("en");

        var fastForward = Review(Plan(BasisUpdateKind.FastForward));
        Assert.True(fastForward.HasPrimary);
        Assert.Equal(BasisUpdateDecision.Update, fastForward.PrimaryDecision);
        Assert.True(fastForward.ShowBackup);
        Assert.Equal(2, fastForward.Commits.Count);
        Assert.True(fastForward.HasMoreCommits);

        var blocked = Review(new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.Merge, BasisBranch = "developer", HeadSha = "h", UpstreamSha = "u", MergeBase = "b",
            BlockingPaths = new[] { "Packages/com.example/package.json" },
        });
        Assert.False(blocked.HasPrimary);
        Assert.True(blocked.HasBlocking);

        var metaInTheWay = Review(new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.Merge, BasisBranch = "developer", HeadSha = "h", UpstreamSha = "u", MergeBase = "b",
            ReplacedMetaPaths = new[] { "Assets/Tools.meta", "Assets/Art.meta" },
        });
        Assert.True(metaInTheWay.HasPrimary);
        Assert.False(metaInTheWay.HasBlocking);
        Assert.True(metaInTheWay.HasReplacedMeta);
        Assert.StartsWith("2 Unity .meta files", metaInTheWay.ReplacedMetaNote);
        Assert.False(fastForward.HasReplacedMeta);

        var copied = Review(new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.Apply, BasisBranch = "developer", FromBranch = "developer", LocalBranch = "main", HeadSha = "h", UpstreamSha = "u",
            MergeBase = "b", ApplyCommit = "c", BaseSource = BasisBaseSource.Similarity, IncomingCount = 3,
            SuggestedBase = new BasisBaseMatch("sha", "shortsha", DateTimeOffset.Now, "subject", 10016, 10017),
        });
        Assert.Equal(BasisUpdateDecision.Update, copied.PrimaryDecision);
        Assert.Equal("Update Basis", copied.PrimaryLabel);
        Assert.True(copied.HasMatch);
        Assert.Contains("10,016", copied.MatchText);
        Assert.True(copied.IsSeparateHistory);
        Assert.Contains("main", copied.SeparateHistoryNote);
        Assert.False(copied.IsSwitch);

        Assert.False(Review(new BasisUpdatePlan { Kind = BasisUpdateKind.Unrelated, BasisBranch = "developer" }).HasPrimary);

        var notGit = Review(new BasisUpdatePlan { Kind = BasisUpdateKind.NotGitRepo, BasisBranch = "developer" });
        Assert.Equal(BasisUpdateDecision.SetUpGit, notGit.PrimaryDecision);
        Assert.False(notGit.ShowChangeBranch);

        var upToDate = Review(Plan(BasisUpdateKind.UpToDate));
        Assert.False(upToDate.HasPrimary);
        Assert.Equal("Close", upToDate.CancelLabel);

        Assert.False(Review(new BasisUpdatePlan { Kind = BasisUpdateKind.Blocked, Block = BasisUpdateBlock.DetachedHead }).ShowChangeBranch);
        Assert.True(Review(new BasisUpdatePlan { Kind = BasisUpdateKind.Blocked, Block = BasisUpdateBlock.BranchNotFound }).ShowChangeBranch);
    }

    [AvaloniaFact]
    public async Task Review_replans_when_another_basis_branch_is_picked()
    {
        Localizer.Instance.SetLanguage("en");
        var lts = Plan(BasisUpdateKind.FastForward, "long-term-support-20260916") ;
        var review = Review(Plan(BasisUpdateKind.FastForward), branch => Task.FromResult<BasisUpdatePlan?>(new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.FastForward, BasisBranch = branch, LocalBranch = "my-game", HeadSha = "h", UpstreamSha = "u", MergeBase = "h",
            IncomingCount = 1, IncomingCommits = Commits.Take(1).ToList(),
        }));
        var raised = new HashSet<string?>();
        review.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await review.SwitchBranchAsync(lts.BasisBranch);

        Assert.Equal("long-term-support-20260916", review.BasisBranch);
        Assert.True(review.BranchChanged);
        Assert.Equal("1 new Basis commit", review.IncomingLabel);
        Assert.Contains(nameof(BasisUpdateReviewViewModel.IncomingLabel), raised);
        Assert.Contains(nameof(BasisUpdateReviewViewModel.Commits), raised);
        Assert.False(review.IsWorking);
    }

    [AvaloniaFact]
    public void Review_describes_moving_to_another_basis_branch()
    {
        Localizer.Instance.SetLanguage("en");
        var lts = "long-term-support-20260916";
        var review = Review(new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.Apply, BasisBranch = lts, FromBranch = "developer", LocalBranch = "main", HeadSha = "h", UpstreamSha = "u",
            MergeBase = "0123456789abcdef", ApplyCommit = "c", Connected = true, BaseSource = BasisBaseSource.History, OutgoingCount = 45,
            Layout = new BasisLayout("Basis", ""),
        });

        Assert.True(review.IsSwitch);
        Assert.Equal("Change Basis branch", review.HeaderText);
        Assert.Equal($"Switch to {lts}", review.PrimaryLabel);
        Assert.Contains("developer", review.SwitchSummary);
        Assert.True(review.HasOutgoing);
        Assert.Contains("45", review.OutgoingLabel);
        Assert.Contains("012345678", review.ModeNote);
        Assert.False(review.IsSeparateHistory);
        Assert.True(review.HasLayout);
        Assert.Contains("Basis", review.LayoutNote);
        Assert.False(review.HasCommits);

        var follow = Review(new BasisUpdatePlan { Kind = BasisUpdateKind.UpToDate, BasisBranch = lts, FromBranch = "developer", UpstreamSha = "u" });
        Assert.True(follow.HasPrimary);
        Assert.Equal(BasisUpdateDecision.Follow, follow.PrimaryDecision);
        Assert.Equal($"Follow {lts}", follow.PrimaryLabel);
        Assert.Contains(lts, follow.UpToDateBody);
    }

    [AvaloniaFact]
    public void Branch_picker_offers_basis_and_project_branches_separately()
    {
        Localizer.Instance.SetLanguage("en");
        var groups = InstallsViewModel.BranchGroups(new[] { "developer", "long-term-support-20260916" }, "long-term-support-20260916",
            new[] { new ProjectBranch("main", null, true), new ProjectBranch("team-feature", "origin", false) });

        Assert.Equal(2, groups.Count);
        Assert.All(groups[0].Items, i => Assert.True(i.IsBasis));
        Assert.True(groups[0].Items.Single(i => i.IsCurrent).Name == "long-term-support-20260916");
        Assert.Equal(new[] { "main", "origin/team-feature" }, groups[1].Items.Select(i => i.Label));
        Assert.Equal("team-feature", groups[1].Items[1].Name);
        Assert.Equal("origin", groups[1].Items[1].Remote);

        var window = new BranchPickerWindow("Switch branch", "On main", groups);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("long-term-support-20260916", window.Selected?.Name);
    }

    [AvaloniaFact]
    public void Paused_branch_switch_is_labelled_as_a_switch()
    {
        Localizer.Instance.SetLanguage("en");
        var row = Row("Game");
        row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "feature", "tip", null, true, null, BasisOperation.BranchSwitch));

        Assert.True(row.BranchSwitchInProgress);
        Assert.Equal("Finish switch", row.UpdateButtonLabel);
        Assert.Equal("Branch switch paused", row.BasisPillText);

        var model = new MergeConflictsViewModel("My Game", Path.GetTempPath(),
            () => Task.FromResult<BasisUpdateState?>(new BasisUpdateState { Phase = BasisUpdatePhase.RestoringChanges, Operation = BasisOperation.BranchSwitch, TargetBranch = "feature" }),
            () => Task.FromResult<IReadOnlyList<BasisConflict>>(new[] { new BasisConflict("a.txt", ConflictKind.DeletedByBasis, false) }),
            (_, _) => Task.FromResult(BasisStepResult.Success),
            () => Task.FromResult(BasisUpdateResult.Updated(null)),
            () => Task.FromResult(BasisUpdateResult.Aborted(null)),
            () => false);
        model.LoadAsync().GetAwaiter().GetResult();

        Assert.True(model.IsBranchSwitch);
        Assert.Equal("Use branch version", model.UseTheirsLabel);
        Assert.Equal("Undo switch", model.AbortLabel);
        Assert.Equal("Finish switch", model.FinishLabel);
        Assert.Contains("feature", model.PhaseText);
        Assert.Contains("feature", model.Conflicts.Single().KindLabel);
        Assert.Equal("Finish switching branch", model.TitleText);
    }

    [AvaloniaFact]
    public void Review_window_loads_for_every_kind_of_plan()
    {
        var plans = new[]
        {
            Plan(BasisUpdateKind.Merge),
            Plan(BasisUpdateKind.FastForward),
            Plan(BasisUpdateKind.UpToDate),
            new BasisUpdatePlan { Kind = BasisUpdateKind.Unrelated, BasisBranch = "developer" },
            new BasisUpdatePlan { Kind = BasisUpdateKind.NotGitRepo, BasisBranch = "developer" },
            new BasisUpdatePlan { Kind = BasisUpdateKind.Blocked, Block = BasisUpdateBlock.FetchFailed, Detail = "offline" },
            new BasisUpdatePlan { Kind = BasisUpdateKind.Apply, BasisBranch = "long-term-support-20260916", FromBranch = "developer", MergeBase = "b", OutgoingCount = 3 },
            new BasisUpdatePlan { Kind = BasisUpdateKind.UpToDate, BasisBranch = "long-term-support-20260916", FromBranch = "developer" },
        };
        foreach (var plan in plans)
        {
            var review = Review(plan);
            var window = new BasisUpdateWindow(review);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(review, window.DataContext);
        }
    }

    [AvaloniaFact]
    public void Conflicts_dialog_finishes_once_every_file_is_decided()
    {
        Localizer.Instance.SetLanguage("en");
        var remaining = new List<BasisConflict>
        {
            new("Assets/Scenes/Main.unity", ConflictKind.BothChanged, true),
            new("Assets/Scripts/Game.cs", ConflictKind.DeletedByBasis, false),
        };
        var decisions = new List<(string Path, ConflictChoice Choice)>();
        BasisUpdateResult? completed = null;
        var model = new MergeConflictsViewModel("My Game", Path.GetTempPath(),
            () => Task.FromResult<BasisUpdateState?>(new BasisUpdateState { Phase = BasisUpdatePhase.RestoringChanges }),
            () => Task.FromResult<IReadOnlyList<BasisConflict>>(remaining.ToList()),
            (path, choice) =>
            {
                decisions.Add((path, choice));
                remaining.RemoveAll(c => c.Path == path);
                return Task.FromResult(BasisStepResult.Success);
            },
            () => Task.FromResult(BasisUpdateResult.Updated("new-head")),
            () => Task.FromResult(BasisUpdateResult.Aborted("old-head")),
            () => false);
        model.Completed += result => completed = result;

        model.LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(BasisUpdatePhase.RestoringChanges, model.Phase);
        Assert.Equal(2, model.Conflicts.Count);
        Assert.False(model.CanFinish);

        model.KeepMineCommand.Execute(model.Conflicts[0]);
        Assert.Single(model.Conflicts);
        model.UseAllBasisCommand.Execute(null);
        Assert.Empty(model.Conflicts);
        Assert.True(model.CanFinish);

        model.FinishCommand.Execute(null);

        Assert.Equal(new[] { ("Assets/Scenes/Main.unity", ConflictChoice.Mine), ("Assets/Scripts/Game.cs", ConflictChoice.Basis) }, decisions);
        Assert.Equal(BasisUpdateResultKind.Updated, completed?.Kind);
    }

    [AvaloniaFact]
    public void Conflicts_dialog_refuses_to_touch_files_while_unity_has_the_project_open()
    {
        Localizer.Instance.SetLanguage("en");
        var resolved = false;
        var model = new MergeConflictsViewModel("My Game", Path.GetTempPath(),
            () => Task.FromResult<BasisUpdateState?>(null),
            () => Task.FromResult<IReadOnlyList<BasisConflict>>(new[] { new BasisConflict("a.txt", ConflictKind.BothChanged, false) }),
            (_, _) => { resolved = true; return Task.FromResult(BasisStepResult.Success); },
            () => Task.FromResult(BasisUpdateResult.Updated(null)),
            () => Task.FromResult(BasisUpdateResult.Aborted(null)),
            () => true);
        model.LoadAsync().GetAwaiter().GetResult();

        model.KeepMineCommand.Execute(model.Conflicts[0]);

        Assert.False(resolved);
        Assert.True(model.HasMessage);
    }

    [AvaloniaFact]
    public void Conflicts_window_loads()
    {
        var model = new MergeConflictsViewModel("My Game", Path.GetTempPath(),
            () => Task.FromResult<BasisUpdateState?>(new BasisUpdateState()),
            () => Task.FromResult<IReadOnlyList<BasisConflict>>(new[] { new BasisConflict("a.txt", ConflictKind.BothAdded, false) }),
            (_, _) => Task.FromResult(BasisStepResult.Success),
            () => Task.FromResult(BasisUpdateResult.Updated(null)),
            () => Task.FromResult(BasisUpdateResult.Aborted(null)),
            () => false);
        model.LoadAsync().GetAwaiter().GetResult();
        var window = new MergeConflictsWindow(model);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(model, window.DataContext);
        Assert.Single(model.Conflicts);
    }

    [AvaloniaFact]
    public void Shell_badge_and_banner_track_projects_with_basis_updates()
    {
        Localizer.Instance.SetLanguage("en");
        var shell = new MainWindowViewModel();
        var game = Row("Game");
        var other = Row("Other");
        shell.InstallsVM.Installs.Add(game);
        shell.InstallsVM.Installs.Add(other);

        shell.RefreshBasisUpdateNotice();
        Assert.False(shell.HasBasisUpdates);
        Assert.False(shell.BasisBannerVisible);

        game.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "developer", "tip", 12, false));
        other.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpToDate, "developer", "tip", 0, false));
        shell.RefreshBasisUpdateNotice();
        Assert.Equal(1, shell.BasisUpdateCount);
        Assert.True(shell.BasisBannerVisible);
        Assert.Contains("Game", shell.BasisBannerText);
        Assert.Contains("12", shell.BasisBannerText);

        other.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "developer", "tip", null, true));
        shell.RefreshBasisUpdateNotice();
        Assert.Equal(2, shell.BasisUpdateCount);
        Assert.Contains("2", shell.BasisBannerText);
    }
}
