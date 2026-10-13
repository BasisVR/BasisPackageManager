using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ProjectStorageViewModel : ObservableObject
{
    private readonly BasisInstall _install;
    private readonly ProjectStorageService _storage;
    private readonly Func<string, Func<Task>, Task> _queue;
    private readonly Func<string, string, Task<bool>> _confirm;
    private readonly Action<string, bool>? _report;
    private CancellationTokenSource? _measure;
    private ProjectStorageReport? _last;
    private IReadOnlyList<UnusedClone> _unused = Array.Empty<UnusedClone>();
    private bool _isMeasuring;
    private bool _isWorking;
    private bool _stopped;
    private long _progressFiles;
    private long _progressBytes;
    private string _status = "";
    private bool _statusIsError;

    public ProjectStorageViewModel(BasisInstall install, string projectName, ProjectStorageService storage, Func<string, Func<Task>, Task>? queue = null, Func<string, string, Task<bool>>? confirm = null, Action<string, bool>? report = null)
    {
        _install = install;
        ProjectName = projectName;
        _storage = storage;
        _queue = queue ?? ((_, work) => work());
        _confirm = confirm ?? ((_, _) => Task.FromResult(true));
        _report = report;
        RefreshCommand = new RelayCommand(MeasureAsync, () => !_isWorking);
        StopCommand = new RelayCommand(Stop);
        CleanLibraryCommand = new RelayCommand(CleanLibraryAsync);
        ClearTempCommand = new RelayCommand(ClearTempAsync);
        ClearLogsCommand = new RelayCommand(ClearLogsAsync);
        RemoveClonesCommand = new RelayCommand(RemoveClonesAsync);
    }

    public string ProjectName { get; }
    public string Title => L.Tr("dialog.storage.title", ProjectName);
    public string Heading => L.Tr("dialog.storage.heading", ProjectName);
    public ObservableCollection<StorageRow> Rows { get; } = new();
    public ObservableCollection<StorageCloneRow> Clones { get; } = new();
    public RelayCommand RefreshCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CleanLibraryCommand { get; }
    public RelayCommand ClearTempCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand RemoveClonesCommand { get; }
    public ProjectStorageReport? Report => _last;
    public bool IsMeasuring { get => _isMeasuring; private set { if (SetField(ref _isMeasuring, value)) RaiseState(); } }
    public bool IsWorking { get => _isWorking; private set { if (SetField(ref _isWorking, value)) RaiseState(); } }
    public bool IsIdle => !_isMeasuring && !_isWorking;
    public bool HasRows => Rows.Count > 0;
    public bool HasClones => Clones.Count > 0;
    public int UnusedCount => _unused.Count(c => c.Removable);
    public bool HasUnusedClones => UnusedCount > 0;
    public string RemoveClonesLabel => L.Tr("dialog.storage.removeClones", UnusedCount);
    public string StatusText { get => _status; private set { if (SetField(ref _status, value)) RaiseStatus(); } }
    public bool HasStatus => _status.Length > 0;
    public bool HasInfoStatus => HasStatus && !_statusIsError;
    public bool HasErrorStatus => HasStatus && _statusIsError;

    public string ProgressText => _isMeasuring
        ? L.Tr("dialog.storage.measuring", Volatile.Read(ref _progressFiles).ToString("N0"), DiskSpace.Human(Volatile.Read(ref _progressBytes)))
        : _last is { } report ? L.Tr(_stopped ? "dialog.storage.stopped" : "dialog.storage.total", DiskSpace.Human(report.TotalBytes), report.TotalFiles.ToString("N0"))
        : _stopped ? L.Tr("dialog.storage.stoppedEmpty") : "";

    public async Task MeasureAsync()
    {
        _measure?.Cancel();
        var measure = _measure = new CancellationTokenSource();
        _stopped = false;
        Volatile.Write(ref _progressFiles, 0);
        Volatile.Write(ref _progressBytes, 0);
        IsMeasuring = true;
        try
        {
            var progress = new Progress<StorageProgress>(p =>
            {
                Volatile.Write(ref _progressFiles, p.Files);
                Volatile.Write(ref _progressBytes, p.Bytes);
                if (ReferenceEquals(measure, _measure)) OnPropertyChanged(nameof(ProgressText));
            });
            var report = await _storage.MeasureAsync(_install, progress, measure.Token);
            var unused = await LoadUnusedAsync(measure.Token);
            if (!ReferenceEquals(measure, _measure)) return;
            _last = report;
            _unused = unused;
            Build(report);
        }
        catch (OperationCanceledException) { if (ReferenceEquals(measure, _measure)) _stopped = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write($"Measuring {_install.RepoRoot}", ex);
            if (ReferenceEquals(measure, _measure)) ShowError(L.Tr("dialog.storage.measureFailed", ex.Message));
        }
        finally
        {
            if (ReferenceEquals(measure, _measure)) IsMeasuring = false;
        }
    }

    public void Stop() => _measure?.Cancel();

    private async Task<IReadOnlyList<UnusedClone>> LoadUnusedAsync(CancellationToken ct)
    {
        try { return await _storage.FindUnusedClonesAsync(_install, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            DiagnosticLog.Write($"Looking for unused clones in {_install.UnityProjectPath}", ex);
            return Array.Empty<UnusedClone>();
        }
    }

    private void Build(ProjectStorageReport report)
    {
        Rows.Clear();
        if (report.Find(StorageArea.Library) is { } library)
        {
            var cache = report.BytesOf(StorageArea.PackageCache);
            Rows.Add(new StorageRow(L.Tr("dialog.storage.library"), cache > 0 ? L.Tr("dialog.storage.libraryDetailCache", DiskSpace.Human(cache)) : L.Tr("dialog.storage.libraryDetail"),
                library.Bytes, L.Tr("dialog.storage.clean"), L.Tr("dialog.storage.tooltip.cleanLibrary"), CleanLibraryCommand, danger: true));
        }
        if (report.Find(StorageArea.Temp) is { } temp)
            Rows.Add(new StorageRow(L.Tr("dialog.storage.temp"), L.Tr("dialog.storage.tempDetail"), temp.Bytes, L.Tr("dialog.storage.clear"), L.Tr("dialog.storage.tooltip.clearTemp"), ClearTempCommand));
        if (report.Find(StorageArea.Logs) is { } logs)
            Rows.Add(new StorageRow(L.Tr("dialog.storage.logs"), L.Tr("dialog.storage.logsDetail"), logs.Bytes, L.Tr("dialog.storage.clear"), L.Tr("dialog.storage.tooltip.clearLogs"), ClearLogsCommand));
        if (report.Find(StorageArea.Obj) is { } obj)
            Rows.Add(new StorageRow(L.Tr("dialog.storage.obj"), L.Tr("dialog.storage.objDetail"), obj.Bytes));
        if (report.Find(StorageArea.ServerBuild) is { } server)
            Rows.Add(new StorageRow(L.Tr("dialog.storage.server"), L.Tr("dialog.storage.serverDetail"), server.Bytes));
        if (report.Clones.Count > 0)
            Rows.Add(new StorageRow(L.Tr("dialog.storage.clones"), L.Tr("dialog.storage.clonesDetail", report.Clones.Count), report.BytesOf(StorageArea.DevClone)));
        Rows.Add(new StorageRow(L.Tr("dialog.storage.other"), L.Tr("dialog.storage.otherDetail"), report.BytesOf(StorageArea.Other)));

        Clones.Clear();
        foreach (var clone in report.Clones)
        {
            var unused = _unused.FirstOrDefault(u => string.Equals(u.Id, clone.Name, StringComparison.OrdinalIgnoreCase));
            var state = !MountService.IsWorkingClone(clone.Path) ? L.Tr("dialog.storage.cloneLoose")
                : unused is null ? L.Tr("dialog.storage.cloneInUse")
                : unused.Removable ? L.Tr("dialog.storage.cloneUnused")
                : unused.Reason == UnusedCloneReason.Listed ? L.Tr("dialog.storage.cloneListed")
                : L.Tr("dialog.storage.cloneLocalWork");
            Clones.Add(new StorageCloneRow(clone.Name ?? Path.GetFileName(clone.Path), state, clone.Bytes, unused?.Removable == true));
        }
        RaiseState();
    }

    public Task CleanLibraryAsync() => RunAsync(
        L.Tr("dialog.storage.confirmLibraryTitle"),
        L.Tr("dialog.storage.confirmLibraryBody", DiskSpace.Human(_last?.BytesOf(StorageArea.Library) ?? 0), ProjectName),
        L.Tr("dialog.storage.cleaningLibrary", ProjectName),
        requiresClosedUnity: true,
        () => ProjectStorageService.CleanLibraryAsync(_install), DescribeClean);

    public Task ClearTempAsync() => RunAsync(
        L.Tr("dialog.storage.confirmTempTitle"),
        L.Tr("dialog.storage.confirmTempBody", DiskSpace.Human(_last?.BytesOf(StorageArea.Temp) ?? 0), ProjectName),
        L.Tr("dialog.storage.clearingTemp", ProjectName),
        requiresClosedUnity: true,
        () => ProjectStorageService.ClearTempAsync(_install), DescribeClean);

    public Task ClearLogsAsync() => RunAsync(
        L.Tr("dialog.storage.confirmLogsTitle"),
        L.Tr("dialog.storage.confirmLogsBody", DiskSpace.Human(_last?.BytesOf(StorageArea.Logs) ?? 0), ProjectName),
        L.Tr("dialog.storage.clearingLogs", ProjectName),
        requiresClosedUnity: false,
        () => ProjectStorageService.ClearLogsAsync(_install), DescribeClean);

    public Task RemoveClonesAsync()
    {
        var removable = _unused.Where(c => c.Removable).Select(c => c.Id).ToList();
        if (removable.Count == 0) return Task.CompletedTask;
        return RunAsync(
            L.Tr("dialog.storage.confirmClonesTitle"),
            L.Tr("dialog.storage.confirmClonesBody", removable.Count, string.Join(", ", removable)),
            L.Tr("dialog.storage.removingClones", ProjectName),
            requiresClosedUnity: false,
            () => _storage.RemoveUnusedClonesAsync(_install, removable), DescribeClones);
    }

    private async Task RunAsync(string title, string body, string label, bool requiresClosedUnity, Func<Task<StorageCleanResult>> work, Func<StorageCleanResult, string> describe)
    {
        if (!IsIdle) return;
        if (requiresClosedUnity && UnityProjectService.IsOpenInUnity(_install.UnityProjectPath))
        {
            ShowError(L.Tr("dialog.storage.closeUnity", ProjectName));
            return;
        }
        if (!await _confirm(title, body)) return;
        IsWorking = true;
        StatusText = "";
        try
        {
            StorageCleanResult? result = null;
            await _queue(label, async () => result = await work());
            if (result is null) return;
            var message = describe(result);
            if (result.Ok) ShowInfo(message);
            else ShowError(message);
            _report?.Invoke(message, result.Ok);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write($"{label} ({_install.RepoRoot})", ex);
            ShowError(L.Tr("dialog.storage.failed", ex.Message));
        }
        finally { IsWorking = false; }
        await MeasureAsync();
    }

    private static string DescribeClean(StorageCleanResult result)
    {
        if (result.Ok) return result.Freed > 0 ? L.Tr("dialog.storage.freed", DiskSpace.Human(result.Freed)) : L.Tr("dialog.storage.nothingToClean");
        return result.Skipped.Count > 0 ? L.Tr("dialog.storage.partly", DiskSpace.Human(result.Freed), result.Skipped.Count) : L.Tr("dialog.storage.failed", result.Message);
    }

    private static string DescribeClones(StorageCleanResult result)
    {
        if (result.Removed.Count == 0 && result.Skipped.Count == 0) return L.Tr("dialog.storage.nothingToClean");
        var removed = L.Tr("dialog.storage.clonesRemoved", result.Removed.Count, DiskSpace.Human(result.Freed));
        return result.Ok ? removed : removed + " " + L.Tr("dialog.storage.clonesKept", string.Join("; ", result.Skipped));
    }

    private void ShowInfo(string message)
    {
        _statusIsError = false;
        StatusText = message;
        RaiseStatus();
    }

    private void ShowError(string message)
    {
        _statusIsError = true;
        StatusText = message;
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasInfoStatus));
        OnPropertyChanged(nameof(HasErrorStatus));
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasClones));
        OnPropertyChanged(nameof(UnusedCount));
        OnPropertyChanged(nameof(HasUnusedClones));
        OnPropertyChanged(nameof(RemoveClonesLabel));
        RefreshCommand.Raise();
    }
}

public sealed class StorageRow
{
    public StorageRow(string label, string detail, long bytes, string? actionLabel = null, string? actionTooltip = null, RelayCommand? action = null, bool danger = false)
    {
        Label = label;
        Detail = detail;
        Bytes = bytes;
        ActionLabel = actionLabel ?? "";
        ActionTooltip = actionTooltip ?? "";
        Action = action;
        IsDanger = danger;
    }

    public string Label { get; }
    public string Detail { get; }
    public long Bytes { get; }
    public string SizeText => DiskSpace.Human(Bytes);
    public string ActionLabel { get; }
    public string ActionTooltip { get; }
    public RelayCommand? Action { get; }
    public bool HasAction => Action is not null;
    public bool HasSafeAction => Action is not null && !IsDanger;
    public bool HasDangerAction => Action is not null && IsDanger;
    public bool IsDanger { get; }
}

public sealed class StorageCloneRow(string id, string state, long bytes, bool unused)
{
    public string Id { get; } = id;
    public string State { get; } = state;
    public long Bytes { get; } = bytes;
    public string SizeText => DiskSpace.Human(Bytes);
    public bool IsUnused { get; } = unused;
}
