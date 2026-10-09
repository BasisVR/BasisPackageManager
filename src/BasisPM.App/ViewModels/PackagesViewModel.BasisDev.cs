using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private BasisDevService? _basisDevService;
    private BasisDevReport _basisDevReport = BasisDevReport.Empty;
    private Dictionary<string, BasisDevItem> _basisDevItems = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<BasisDevPackage> _basisDevStale = Array.Empty<BasisDevPackage>();
    private BasisInstall? _basisDevItemsInstall;
    private string? _basisDevItemsLanguage;
    private bool _basisDevItemsDirty = true;
    private bool _scanningBasisDev;
    private bool _rescanBasisDev;
    private RelayCommand<BasisDevChoice>? _runBasisDevChoiceCommand;
    private RelayCommand? _fixBasisDevRecordsCommand;
    private RelayCommand<BasisDevItem>? _openBasisDevFolderCommand;
    private RelayCommand<PackageRow>? _sendToBasisCommand;

    private BasisDevService BasisDev => _basisDevService ??= new BasisDevService(_gitService, _projectService, _mountRegistry, _mountService);

    public ObservableCollection<BasisDevItem> BasisDevIssues { get; } = new();
    public bool HasBasisDevIssues => BasisDevIssues.Count > 0 || _basisDevStale.Count > 0;
    public bool HasBasisDevStale => _basisDevStale.Count > 0;
    public bool CanFixBasisDevRecords => _basisDevReport.SafeFixes.Count > 0;
    public string BasisDevSummary => L.Tr("basisdev.banner.summary", BasisDevIssues.Count + _basisDevStale.Count);
    public string BasisDevStaleText => L.Tr("basisdev.stale.summary", _basisDevStale.Count, string.Join(", ", _basisDevStale.Select(p => p.Id)));

    public RelayCommand<BasisDevChoice> RunBasisDevChoiceCommand => _runBasisDevChoiceCommand ??= new RelayCommand<BasisDevChoice>(RunBasisDevChoiceAsync);
    public RelayCommand FixBasisDevRecordsCommand => _fixBasisDevRecordsCommand ??= new RelayCommand(FixBasisDevRecordsAsync);
    public RelayCommand<BasisDevItem> OpenBasisDevFolderCommand => _openBasisDevFolderCommand ??= new RelayCommand<BasisDevItem>(OpenBasisDevFolder);
    public RelayCommand<PackageRow> SendToBasisCommand => _sendToBasisCommand ??= new RelayCommand<PackageRow>(row =>
        row is null || _install is null ? Task.CompletedTask : _shell.OpenBasisChangesAsync(_install, row.Name));

    private IEnumerable<MountRecord> ActiveMounts(BasisInstall install)
    {
        var report = BasisDev.Scan(install);
        foreach (var mount in _mountService.ListMounts(install.UnityProjectPath))
            if (report.Find(mount.PackageId) is { CloneActive: true }) yield return mount;
    }

    private void ClearBasisDevState()
    {
        _basisDevReport = BasisDevReport.Empty;
        _basisDevItemsDirty = true;
        ApplyBasisDevState();
    }

    private async Task ScanBasisDevAsync()
    {
        if (_scanningBasisDev) { _rescanBasisDev = true; return; }
        if (_install is null || !_install.HasUnityProject) { ClearBasisDevState(); return; }
        _scanningBasisDev = true;
        try
        {
            var install = _install;
            var report = await Task.Run(() => BasisDev.ScanAsync(install));
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(install, _install)) return;
                _basisDevReport = report;
                _basisDevItemsDirty = true;
                ApplyBasisDevState();
            });
        }
        catch (Exception ex) { DiagnosticLog.Write("Scanning the project's package sources and .basisdev clones", ex); }
        finally
        {
            _scanningBasisDev = false;
            if (_rescanBasisDev) { _rescanBasisDev = false; _ = ScanBasisDevAsync(); }
        }
    }

    private void ApplyBasisDevState()
    {
        var install = _install;
        if (_basisDevItemsDirty || !ReferenceEquals(install, _basisDevItemsInstall) || _basisDevItemsLanguage != Localizer.Instance.CurrentCode)
            RebuildBasisDevItems(install);
        foreach (var row in Available) row.BasisDev = _basisDevItems.GetValueOrDefault(row.Name);
    }

    private void RebuildBasisDevItems(BasisInstall? install)
    {
        _basisDevItemsDirty = false;
        _basisDevItemsInstall = install;
        _basisDevItemsLanguage = Localizer.Instance.CurrentCode;
        _basisDevItems = new Dictionary<string, BasisDevItem>(StringComparer.OrdinalIgnoreCase);
        BasisDevIssues.Clear();
        var stale = new List<BasisDevPackage>();
        if (install is not null && string.Equals(_basisDevReport.UnityProjectPath, install.UnityProjectPath, StringComparison.Ordinal))
        {
            foreach (var package in _basisDevReport.Packages)
            {
                var item = new BasisDevItem(package, install);
                _basisDevItems[package.Id] = item;
                if (package.Issues.Count == 1 && package.Issues[0] == BasisDevIssueKind.StaleRecord) stale.Add(package);
                else if (package.Issues.Count > 0) BasisDevIssues.Add(item);
            }
        }
        _basisDevStale = stale;
        OnPropertyChanged(nameof(HasBasisDevIssues));
        OnPropertyChanged(nameof(HasBasisDevStale));
        OnPropertyChanged(nameof(CanFixBasisDevRecords));
        OnPropertyChanged(nameof(BasisDevSummary));
        OnPropertyChanged(nameof(BasisDevStaleText));
    }

    private async Task RunBasisDevChoiceAsync(BasisDevChoice? choice)
    {
        if (choice is null || _install is null || !_install.HasUnityProject) return;
        var install = _install;
        var id = choice.Item.Id;
        IsBusy = true;
        SetStatus(L.Tr("basisdev.status.working", id));
        try
        {
            void Progress(string line) => Avalonia.Threading.Dispatcher.UIThread.Post(() => SetStatus(line));
            var result = await BasisDev.ApplyAsync(install, id, choice.Action, force: false, Progress);
            if (!result.Ok && result.NeedsForce)
            {
                var confirmed = choice.Action == BasisDevAction.UseClone
                    ? await Dialogs.ConfirmAsync(L.Tr("basisdev.confirm.committedTitle"), L.Tr("basisdev.confirm.committedBody", id, choice.Item.Package.ManifestValue ?? ""))
                    : await Dialogs.ConfirmAsync(L.Tr("basisdev.confirm.localWorkTitle"), L.Tr("basisdev.confirm.localWorkBody", id, choice.Item.Clone ?? ""));
                if (!confirmed)
                {
                    SetStatus(L.Tr("basisdev.status.cancelled", id), StatusKind.Info);
                    return;
                }
                result = await BasisDev.ApplyAsync(install, id, choice.Action, force: true, Progress);
            }
            if (result.Ok)
            {
                _mountEditedIds.Remove(id);
                _mountEditSummaries.Remove(id);
            }
            SetStatus(result.Ok ? SucceededText(choice.Action, id) : L.Tr("basisdev.status.failed", id, result.Message), result.Ok ? StatusKind.Success : StatusKind.Error);
            await ReloadInstalledAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Reconciling {id}", ex);
            SetStatus(L.Tr("basisdev.status.failed", id, ex.Message), StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    private async Task FixBasisDevRecordsAsync()
    {
        if (_install is null || !_install.HasUnityProject) return;
        var install = _install;
        var recorded = _basisDevReport.Packages.Count(p => p.Actions.Contains(BasisDevAction.Record));
        var forgotten = _basisDevReport.Packages.Count(p => p.Actions.Contains(BasisDevAction.Prune));
        IsBusy = true;
        try
        {
            var result = await BasisDev.ReconcileAsync(install);
            SetStatus(result.Ok ? L.Tr("basisdev.status.reconciled", recorded, forgotten) : L.Tr("basisdev.status.reconcileFailed", result.Message),
                result.Ok ? StatusKind.Success : StatusKind.Error);
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Reconciling .basisdev records", ex);
            SetStatus(L.Tr("basisdev.status.reconcileFailed", ex.Message), StatusKind.Error);
        }
        finally { IsBusy = false; }
    }

    private void OpenBasisDevFolder(BasisDevItem? item)
    {
        var folder = Directory.Exists(item?.Package.CloneFolder) ? item!.Package.CloneFolder : item?.Package.EmbeddedFolder;
        if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) ExternalLink.OpenFolder(folder);
        else if (item is not null) SetStatus(L.Tr("packages.status.mountFolderMissing", item.Id), StatusKind.Error);
    }

    private static string SucceededText(BasisDevAction action, string id) => action switch
    {
        BasisDevAction.Record => L.Tr("basisdev.status.recorded", id),
        BasisDevAction.Prune => L.Tr("basisdev.status.forgotten", id),
        BasisDevAction.UseClone => L.Tr("basisdev.status.usingClone", id),
        BasisDevAction.Release => L.Tr("basisdev.status.released", id),
        BasisDevAction.Restore => L.Tr("basisdev.status.restored", id),
        BasisDevAction.Ignore => L.Tr("basisdev.status.ignored", id),
        BasisDevAction.Unignore => L.Tr("basisdev.status.unignored", id),
        _ => L.Tr("basisdev.status.recloned", id),
    };
}

public sealed partial class PackageRow
{
    private BasisDevItem? _basisDev;

    public BasisDevItem? BasisDev
    {
        get => _basisDev;
        set
        {
            if (!SetField(ref _basisDev, value)) return;
            OnPropertyChanged(nameof(HasBasisDev));
            OnPropertyChanged(nameof(HasBasisDevIssue));
            OnPropertyChanged(nameof(BasisDevIssueLabel));
            OnPropertyChanged(nameof(CanSendToBasis));
        }
    }

    public bool CanSendToBasis => IsIncluded && !IsMounted && _basisDev?.Package is { EmbeddedTracked: true, CloneActive: false };

    public bool HasBasisDev => _basisDev is not null && _basisDev.Package.Source != PackageSourceKind.None;
    public bool HasBasisDevIssue => _basisDev?.HasIssue == true;
    public string? BasisDevIssueLabel => _basisDev?.IssueLabel;
}
