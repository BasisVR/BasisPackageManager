using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ConflictRow
{
    public ConflictRow(BasisConflict conflict, string repoRoot, string? branch = null)
    {
        Path = conflict.Path;
        Kind = conflict.Kind;
        IsUnityAsset = conflict.IsUnityAsset;
        FullPath = System.IO.Path.Combine(repoRoot, conflict.Path);
        KindLabel = branch is null ? BasisUpdateText.DescribeConflict(conflict.Kind) : BasisUpdateText.DescribeBranchConflict(conflict.Kind, branch);
    }

    public string Path { get; }
    public string FullPath { get; }
    public ConflictKind Kind { get; }
    public string KindLabel { get; }
    public bool IsUnityAsset { get; }
    public bool CanOpen => !IsUnityAsset && ExternalLink.CanOpenFile(FullPath) && File.Exists(FullPath);
}

public sealed class MergeConflictsViewModel : ObservableObject
{
    private readonly string _repoRoot;
    private readonly Func<Task<BasisUpdateState?>> _loadState;
    private readonly Func<Task<IReadOnlyList<BasisConflict>>> _loadConflicts;
    private readonly Func<string, ConflictChoice, Task<BasisStepResult>> _resolve;
    private readonly Func<Task<BasisUpdateResult>> _finish;
    private readonly Func<Task<BasisUpdateResult>> _abort;
    private readonly Func<bool> _isUnityOpen;
    private BasisUpdatePhase _phase = BasisUpdatePhase.Merging;
    private BasisOperation _operation = BasisOperation.Merge;
    private string _branch = "";
    private bool _isWorking;
    private string _message = "";

    public MergeConflictsViewModel(string projectName, string repoRoot,
        Func<Task<BasisUpdateState?>> loadState,
        Func<Task<IReadOnlyList<BasisConflict>>> loadConflicts,
        Func<string, ConflictChoice, Task<BasisStepResult>> resolve,
        Func<Task<BasisUpdateResult>> finish,
        Func<Task<BasisUpdateResult>> abort,
        Func<bool> isUnityOpen)
    {
        ProjectName = projectName;
        _repoRoot = repoRoot;
        _loadState = loadState;
        _loadConflicts = loadConflicts;
        _resolve = resolve;
        _finish = finish;
        _abort = abort;
        _isUnityOpen = isUnityOpen;
        KeepMineCommand = new RelayCommand<ConflictRow>(row => ResolveAsync(row, ConflictChoice.Mine));
        UseBasisCommand = new RelayCommand<ConflictRow>(row => ResolveAsync(row, ConflictChoice.Basis));
        MarkDoneCommand = new RelayCommand<ConflictRow>(row => ResolveAsync(row, ConflictChoice.Resolved));
        OpenFileCommand = new RelayCommand<ConflictRow>(row => ExternalLink.OpenFile(row?.FullPath));
        OpenFolderCommand = new RelayCommand<ConflictRow>(row => ExternalLink.OpenFolder(row is null ? null : System.IO.Path.GetDirectoryName(row.FullPath)));
        KeepAllMineCommand = new RelayCommand(() => ResolveAllAsync(ConflictChoice.Mine));
        UseAllBasisCommand = new RelayCommand(() => ResolveAllAsync(ConflictChoice.Basis));
        RefreshCommand = new RelayCommand(LoadAsync);
        FinishCommand = new RelayCommand(FinishAsync);
    }

    public event Action<BasisUpdateResult>? Completed;

    public string ProjectName { get; }
    public ObservableCollection<ConflictRow> Conflicts { get; } = new();

    public RelayCommand<ConflictRow> KeepMineCommand { get; }
    public RelayCommand<ConflictRow> UseBasisCommand { get; }
    public RelayCommand<ConflictRow> MarkDoneCommand { get; }
    public RelayCommand<ConflictRow> OpenFileCommand { get; }
    public RelayCommand<ConflictRow> OpenFolderCommand { get; }
    public RelayCommand KeepAllMineCommand { get; }
    public RelayCommand UseAllBasisCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand FinishCommand { get; }

    public BasisUpdatePhase Phase
    {
        get => _phase;
        private set { if (SetField(ref _phase, value)) OnPropertyChanged(nameof(PhaseText)); }
    }

    public BasisOperation Operation => _operation;
    public bool IsBranchSwitch => _operation == BasisOperation.BranchSwitch;

    public string PhaseText => IsBranchSwitch ? L.Tr("dialog.conflicts.switchRestoring", _branch)
        : _phase == BasisUpdatePhase.Merging ? L.Tr("dialog.conflicts.merging")
        : L.Tr("dialog.conflicts.restoring");

    public string TitleText => IsBranchSwitch ? L.Tr("dialog.conflicts.switchTitle") : L.Tr("dialog.conflicts.title");
    public string UseTheirsLabel => IsBranchSwitch ? L.Tr("dialog.conflicts.useBranch") : L.Tr("dialog.conflicts.useBasis");
    public string UseAllTheirsLabel => IsBranchSwitch ? L.Tr("dialog.conflicts.useAllBranch") : L.Tr("dialog.conflicts.useAllBasis");
    public string UseTheirsTooltip => IsBranchSwitch ? L.Tr("dialog.conflicts.tooltip.useBranch", _branch) : L.Tr("dialog.conflicts.tooltip.useBasis");
    public string UseAllTheirsTooltip => IsBranchSwitch ? L.Tr("dialog.conflicts.tooltip.useAllBranch", _branch) : L.Tr("dialog.conflicts.tooltip.useAllBasis");
    public string AbortLabel => IsBranchSwitch ? L.Tr("dialog.conflicts.abortSwitch") : L.Tr("dialog.conflicts.abort");
    public string AbortTooltip => IsBranchSwitch ? L.Tr("dialog.conflicts.tooltip.abortSwitch") : L.Tr("dialog.conflicts.tooltip.abort");
    public string FinishLabel => IsBranchSwitch ? L.Tr("dialog.conflicts.finishSwitch") : L.Tr("dialog.conflicts.finish");
    public string FinishTooltip => IsBranchSwitch ? L.Tr("dialog.conflicts.tooltip.finishSwitch") : L.Tr("dialog.conflicts.tooltip.finish");
    public string ConfirmAbortTitle => IsBranchSwitch ? L.Tr("dialog.conflicts.confirmAbortSwitchTitle") : L.Tr("dialog.conflicts.confirmAbortTitle");
    public string ConfirmAbortBody => IsBranchSwitch ? L.Tr("dialog.conflicts.confirmAbortSwitchBody") : L.Tr("dialog.conflicts.confirmAbortBody");

    public bool IsWorking
    {
        get => _isWorking;
        private set { if (SetField(ref _isWorking, value)) RaiseCounts(); }
    }

    public bool IsIdle => !_isWorking;
    public bool HasConflicts => Conflicts.Count > 0;
    public bool CanFinish => !_isWorking && Conflicts.Count == 0;
    public string RemainingText => Conflicts.Count > 0 ? L.Tr("dialog.conflicts.remaining", Conflicts.Count)
        : IsBranchSwitch ? L.Tr("dialog.conflicts.allDoneSwitch")
        : L.Tr("dialog.conflicts.allDone");

    public string Message
    {
        get => _message;
        private set { if (SetField(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(_message);

    public async Task LoadAsync()
    {
        IsWorking = true;
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Loading the Basis update conflicts for {_repoRoot}", ex);
            Message = ex.Message;
        }
        finally { IsWorking = false; }
    }

    public async Task AbortAsync()
    {
        if (BlockedByUnity()) return;
        IsWorking = true;
        try
        {
            var result = await _abort();
            if (result.Kind == BasisUpdateResultKind.Aborted) { Completed?.Invoke(result); return; }
            Message = BasisUpdateText.DescribeFailure(result.Failure, result.Detail);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Undoing the Basis update for {_repoRoot}", ex);
            Message = ex.Message;
        }
        finally { IsWorking = false; }
    }

    private async Task ResolveAsync(ConflictRow? row, ConflictChoice choice)
    {
        if (row is null || BlockedByUnity()) return;
        IsWorking = true;
        try
        {
            var result = await _resolve(row.Path, choice);
            Message = result.Ok ? "" : BasisUpdateText.DescribeFailure(result.Failure, result.Detail);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Resolving {row.Path} during a Basis update", ex);
            Message = ex.Message;
        }
        finally { IsWorking = false; }
    }

    private async Task ResolveAllAsync(ConflictChoice choice)
    {
        if (BlockedByUnity()) return;
        IsWorking = true;
        try
        {
            foreach (var row in Conflicts.ToList())
            {
                var result = await _resolve(row.Path, choice);
                if (result.Ok) continue;
                Message = BasisUpdateText.DescribeFailure(result.Failure, result.Detail);
                break;
            }
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Resolving every conflict during a Basis update for {_repoRoot}", ex);
            Message = ex.Message;
        }
        finally { IsWorking = false; }
    }

    private async Task FinishAsync()
    {
        if (BlockedByUnity()) return;
        IsWorking = true;
        try
        {
            var result = await _finish();
            switch (result.Kind)
            {
                case BasisUpdateResultKind.Updated or BasisUpdateResultKind.Aborted:
                    Completed?.Invoke(result);
                    return;
                case BasisUpdateResultKind.Conflicts:
                    Message = "";
                    await ReloadAsync();
                    return;
                default:
                    Message = BasisUpdateText.DescribeFailure(result.Failure, result.Detail);
                    return;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Finishing the Basis update for {_repoRoot}", ex);
            Message = ex.Message;
        }
        finally { IsWorking = false; }
    }

    private async Task ReloadAsync()
    {
        var state = await _loadState();
        if (state is not null)
        {
            Phase = state.Phase;
            _operation = state.Operation;
            _branch = state.TargetBranch ?? "";
            foreach (var name in OperationProperties) OnPropertyChanged(name);
        }
        var conflicts = await _loadConflicts();
        Conflicts.Clear();
        foreach (var conflict in conflicts) Conflicts.Add(new ConflictRow(conflict, _repoRoot, IsBranchSwitch ? _branch : null));
        RaiseCounts();
    }

    private static readonly string[] OperationProperties =
    {
        nameof(Operation), nameof(IsBranchSwitch), nameof(PhaseText), nameof(TitleText), nameof(UseTheirsLabel), nameof(UseAllTheirsLabel),
        nameof(UseTheirsTooltip), nameof(UseAllTheirsTooltip), nameof(AbortLabel), nameof(AbortTooltip), nameof(FinishLabel), nameof(FinishTooltip),
        nameof(ConfirmAbortTitle), nameof(ConfirmAbortBody),
    };

    private bool BlockedByUnity()
    {
        if (!_isUnityOpen()) return false;
        Message = L.Tr("dialog.conflicts.unityOpen");
        return true;
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(RemainingText));
    }
}
