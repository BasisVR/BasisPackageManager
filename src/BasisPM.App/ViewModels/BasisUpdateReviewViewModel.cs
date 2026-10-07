using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public enum BasisUpdateDecision { Cancel, Update, Link, SetUpGit }

public sealed record BasisCommitRow(string ShortSha, string Subject, string Meta);

public sealed class BasisUpdateReviewViewModel : ObservableObject
{
    private readonly Func<string, Task<BasisUpdatePlan?>> _replan;
    private readonly Func<Task<IReadOnlyList<string>>> _listBranches;
    private readonly string _initialBranch;
    private BasisUpdatePlan _plan;
    private bool _isWorking;
    private bool _backup;

    public BasisUpdateReviewViewModel(string projectName, BasisUpdatePlan plan, bool unityOpen,
        Func<string, Task<BasisUpdatePlan?>> replan, Func<Task<IReadOnlyList<string>>> listBranches)
    {
        ProjectName = projectName;
        UnityOpen = unityOpen;
        _plan = plan;
        _initialBranch = plan.BasisBranch;
        _replan = replan;
        _listBranches = listBranches;
    }

    public string ProjectName { get; }
    public bool UnityOpen { get; }
    public BasisUpdatePlan Plan => _plan;
    public bool BranchChanged => !string.Equals(_plan.BasisBranch, _initialBranch, StringComparison.Ordinal);
    public bool IsWorking { get => _isWorking; private set { if (SetField(ref _isWorking, value)) OnPropertyChanged(nameof(CanChangeBranch)); } }
    public bool Backup { get => _backup; set => SetField(ref _backup, value); }

    public string Subtitle => string.IsNullOrEmpty(_plan.LocalBranch)
        ? ProjectName
        : L.Tr("dialog.basisUpdate.sub", ProjectName, _plan.LocalBranch);
    public string BasisBranch => string.IsNullOrEmpty(_plan.BasisBranch) ? BasisInstallService.DefaultBranch : _plan.BasisBranch;
    public bool CanChangeBranch => !IsWorking;
    public bool ShowChangeBranch => _plan.Kind switch
    {
        BasisUpdateKind.NotGitRepo or BasisUpdateKind.InProgress => false,
        BasisUpdateKind.Blocked => _plan.Block is BasisUpdateBlock.BranchNotFound or BasisUpdateBlock.FetchFailed,
        _ => true,
    };

    public bool IsUpdate => _plan.Kind is BasisUpdateKind.FastForward or BasisUpdateKind.Merge;
    public bool IsUpToDate => _plan.Kind == BasisUpdateKind.UpToDate;
    public bool IsUnrelated => _plan.Kind == BasisUpdateKind.Unrelated;
    public bool IsNotGit => _plan.Kind == BasisUpdateKind.NotGitRepo;
    public bool IsBlocked => _plan.Kind == BasisUpdateKind.Blocked;

    public string IncomingLabel => _plan.IncomingCount == 1
        ? L.Tr("dialog.basisUpdate.newCommitsOne")
        : L.Tr("dialog.basisUpdate.newCommits", _plan.IncomingCount);
    public bool HasLocalCommits => _plan.LocalCommitCount > 0;
    public string LocalCommitsLabel => L.Tr("dialog.basisUpdate.yourCommits", _plan.LocalCommitCount);
    public bool HasUncommitted => _plan.UncommittedCount > 0;
    public string UncommittedLabel => L.Tr("dialog.basisUpdate.uncommitted", _plan.UncommittedCount);

    public string ModeNote => _plan.Kind == BasisUpdateKind.FastForward
        ? L.Tr("dialog.basisUpdate.note.fastForward")
        : L.Tr("dialog.basisUpdate.note.merge");
    public bool HasColliding => _plan.CollidingPaths.Count > 0;
    public string CollidingNote => L.Tr("dialog.basisUpdate.note.colliding", _plan.CollidingPaths.Count);
    public bool HasPredictedConflicts => _plan.PredictedConflicts is { Count: > 0 };
    public string PredictedNote => L.Tr("dialog.basisUpdate.note.conflicts", _plan.PredictedConflicts?.Count ?? 0);
    public IReadOnlyList<string> PredictedConflicts => _plan.PredictedConflicts ?? Array.Empty<string>();
    public bool PredictionUnknown => _plan.Kind == BasisUpdateKind.Merge && _plan.PredictedConflicts is null;
    public bool HasBlocking => _plan.BlockingPaths.Count > 0;
    public IReadOnlyList<string> BlockingPaths => _plan.BlockingPaths;

    public IReadOnlyList<BasisCommitRow> Commits => _plan.IncomingCommits
        .Select(c => new BasisCommitRow(c.ShortSha, c.Subject, $"{c.Author} · {c.Date.LocalDateTime:yyyy-MM-dd}"))
        .ToList();
    public bool HasMoreCommits => _plan.IncomingCount > _plan.IncomingCommits.Count;
    public string MoreCommitsLabel => L.Tr("dialog.basisUpdate.moreCommits", _plan.IncomingCount - _plan.IncomingCommits.Count);

    public string UpToDateBody => L.Tr("dialog.basisUpdate.upToDate.body", ProjectName, BasisBranch, Short(_plan.UpstreamSha));
    public bool HasMatch => _plan.SuggestedBase is not null;
    public string MatchText => _plan.SuggestedBase is { } m
        ? L.Tr("dialog.basisUpdate.unrelated.match", m.ShortSha, m.Date.LocalDateTime.ToString("yyyy-MM-dd"), m.Subject,
            m.MatchingFiles.ToString("N0"), m.BasisFiles.ToString("N0"))
        : "";
    public string BlockText => BasisUpdateText.DescribeBlock(_plan);

    public bool HasPrimary => (IsUpdate && _plan.CanApply) || (IsUnrelated && HasMatch) || IsNotGit;
    public BasisUpdateDecision PrimaryDecision => IsUpdate ? BasisUpdateDecision.Update
        : IsUnrelated ? BasisUpdateDecision.Link
        : IsNotGit ? BasisUpdateDecision.SetUpGit
        : BasisUpdateDecision.Cancel;
    public string PrimaryLabel => PrimaryDecision switch
    {
        BasisUpdateDecision.Link => L.Tr("dialog.basisUpdate.link"),
        BasisUpdateDecision.SetUpGit => L.Tr("dialog.basisUpdate.setUpGit"),
        _ => L.Tr("dialog.basisUpdate.update"),
    };
    public string CancelLabel => HasPrimary ? L.Tr("dialog.basisUpdate.cancel") : L.Tr("dialog.basisUpdate.close");
    public bool ShowBackup => IsUpdate && _plan.CanApply;

    public async Task<IReadOnlyList<string>> ListBranchesAsync()
    {
        IsWorking = true;
        try { return await _listBranches(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Listing the Basis branches", ex);
            return Array.Empty<string>();
        }
        finally { IsWorking = false; }
    }

    public async Task SwitchBranchAsync(string branch)
    {
        if (string.Equals(branch, _plan.BasisBranch, StringComparison.Ordinal)) return;
        IsWorking = true;
        try
        {
            var plan = await _replan(branch);
            if (plan is null) return;
            _plan = plan;
            foreach (var name in PlanProperties) OnPropertyChanged(name);
        }
        finally { IsWorking = false; }
    }

    private static readonly string[] PlanProperties =
    {
        nameof(Plan), nameof(BranchChanged), nameof(Subtitle), nameof(BasisBranch), nameof(CanChangeBranch), nameof(ShowChangeBranch),
        nameof(IsUpdate), nameof(IsUpToDate), nameof(IsUnrelated), nameof(IsNotGit), nameof(IsBlocked),
        nameof(IncomingLabel), nameof(HasLocalCommits), nameof(LocalCommitsLabel), nameof(HasUncommitted), nameof(UncommittedLabel),
        nameof(ModeNote), nameof(HasColliding), nameof(CollidingNote), nameof(HasPredictedConflicts), nameof(PredictedNote),
        nameof(PredictedConflicts), nameof(PredictionUnknown), nameof(HasBlocking), nameof(BlockingPaths), nameof(Commits),
        nameof(HasMoreCommits), nameof(MoreCommitsLabel), nameof(UpToDateBody), nameof(HasMatch), nameof(MatchText),
        nameof(BlockText), nameof(HasPrimary), nameof(PrimaryDecision), nameof(PrimaryLabel), nameof(CancelLabel), nameof(ShowBackup),
    };

    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "" : sha.Length > 9 ? sha[..9] : sha;
}

public static class BasisUpdateText
{
    public static string DescribeBlock(BasisUpdatePlan plan) => plan.Block switch
    {
        BasisUpdateBlock.GitMissing => L.Tr("update.block.gitMissing"),
        BasisUpdateBlock.GitTooOld => L.Tr("update.block.gitTooOld", plan.Detail ?? "", BasisUpdateService.MinimumGitVersion.ToString(2)),
        BasisUpdateBlock.DetachedHead => L.Tr("update.block.detached"),
        BasisUpdateBlock.OperationInProgress => L.Tr("update.block.operation", plan.Detail ?? ""),
        BasisUpdateBlock.BranchNotFound => L.Tr("update.block.branchMissing", plan.Detail ?? ""),
        BasisUpdateBlock.ShallowClone => L.Tr("update.block.shallow"),
        BasisUpdateBlock.FetchFailed => L.Tr("update.block.fetch", plan.Detail ?? ""),
        _ => "",
    };

    public static string DescribeFailure(BasisUpdateFailure failure, string? detail)
    {
        var tail = Tail(detail);
        return failure switch
        {
            BasisUpdateFailure.HeadMoved => L.Tr("update.failure.headMoved"),
            BasisUpdateFailure.AlreadyInProgress => L.Tr("update.failure.inProgress"),
            BasisUpdateFailure.NoUpdateInProgress => L.Tr("update.failure.noUpdate"),
            BasisUpdateFailure.IgnoredFilesInTheWay => L.Tr("update.failure.ignoredInTheWay", string.Join(", ", (detail ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(3))),
            BasisUpdateFailure.SetAsideFailed => L.Tr("update.failure.setAside", tail),
            BasisUpdateFailure.MergeFailed => L.Tr("update.failure.merge", tail),
            BasisUpdateFailure.CommitFailed => L.Tr("update.failure.commit", tail),
            BasisUpdateFailure.RestoreFailed => L.Tr("update.failure.restore", tail),
            BasisUpdateFailure.AbortFailed => L.Tr("update.failure.abort", tail),
            BasisUpdateFailure.ResolveFailed => L.Tr("update.failure.resolve", tail),
            BasisUpdateFailure.MarkersRemain => L.Tr("update.failure.markers", detail ?? ""),
            BasisUpdateFailure.LibraryNotIgnored => L.Tr("update.failure.library", detail ?? ""),
            BasisUpdateFailure.InitFailed => L.Tr("update.failure.init", tail),
            BasisUpdateFailure.LinkFailed => L.Tr("update.failure.link", tail),
            _ => L.Tr("update.failure.generic"),
        };
    }

    public static string DescribeConflict(ConflictKind kind) => kind switch
    {
        ConflictKind.BothChanged => L.Tr("dialog.conflicts.kind.bothChanged"),
        ConflictKind.BothAdded => L.Tr("dialog.conflicts.kind.bothAdded"),
        ConflictKind.DeletedByYou => L.Tr("dialog.conflicts.kind.deletedByYou"),
        ConflictKind.DeletedByBasis => L.Tr("dialog.conflicts.kind.deletedByBasis"),
        _ => L.Tr("dialog.conflicts.kind.bothDeleted"),
    };

    private static string Tail(string? text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
