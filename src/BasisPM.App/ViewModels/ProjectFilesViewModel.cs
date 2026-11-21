using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ProjectFilesViewModel : ObservableObject
{
    private readonly BpmService _bpm;
    private readonly Func<Catalog?> _catalog;
    private readonly Func<Action<string>, Task<string?>> _restoreClones;
    private readonly Func<Task> _changed;
    private bool _isBusy;
    private bool _isLoaded;
    private bool _hasManifest;
    private string _progress = "";
    private string _basisDevRoot = "";
    private bool _setupContent;
    private string _setupPackages = "";
    private string _setupAt = BasisInstallService.DefaultBranch;
    private PendingPin? _pending;

    public ProjectFilesViewModel(string projectPath, string projectName, GitService git, Func<Catalog?> catalog, Func<Action<string>, Task<string?>> restoreClones, Func<Task> changed)
    {
        ProjectPath = projectPath;
        ProjectName = projectName;
        _bpm = new BpmService(git);
        _catalog = catalog;
        _restoreClones = restoreClones;
        _changed = changed;
        RefreshCommand = new RelayCommand(LoadAsync, () => !_isBusy);
        LockCommand = new RelayCommand(() => RunAsync(L.Tr("bpm.status.locking"), LockStepsAsync), () => CanAct);
        RestoreCommand = new RelayCommand(() => RunAsync(L.Tr("bpm.status.restoring"), RestoreStepsAsync), () => CanAct);
        MountCommand = new RelayCommand(() => RunAsync(L.Tr("bpm.status.mounting"), async log => log(await _bpm.MountAsync(ProjectPath))), () => CanAct);
        SetupCommand = new RelayCommand(() => RunAsync(L.Tr("bpm.status.settingUp"), SetupStepsAsync), () => !_isBusy && !_hasManifest);
        ApplyPlanCommand = new RelayCommand(() => RunAsync(L.Tr("bpm.status.pinning"), ApplyPendingAsync), () => !_isBusy && _pending is not null);
        CancelPlanCommand = new RelayCommand(CancelPending, () => !_isBusy && _pending is not null);
        OpenBasisDevCommand = new RelayCommand(() => ExternalLink.OpenFolder(_basisDevRoot), () => Directory.Exists(_basisDevRoot));
    }

    public string ProjectPath { get; }
    public string ProjectName { get; }
    public string Heading => L.Tr("bpm.heading", ProjectName);
    public string BasisDevRoot { get => _basisDevRoot; private set => SetField(ref _basisDevRoot, value); }
    public string BasisDevText => L.Tr("bpm.basisdev", _basisDevRoot);
    public ObservableCollection<BpmCheckoutItem> Checkouts { get; } = new();
    public ObservableCollection<BpmPackageItem> Packages { get; } = new();
    public ObservableCollection<BpmMessageItem> Messages { get; } = new();
    public ObservableCollection<BpmTemplateChange> PlanChanges { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand RestoreCommand { get; }
    public RelayCommand MountCommand { get; }
    public RelayCommand SetupCommand { get; }
    public RelayCommand ApplyPlanCommand { get; }
    public RelayCommand CancelPlanCommand { get; }
    public RelayCommand OpenBasisDevCommand { get; }

    public bool IsBusy { get => _isBusy; private set { if (SetField(ref _isBusy, value)) RaiseState(); } }
    public string Progress { get => _progress; private set => SetField(ref _progress, value); }
    public bool HasManifest => _hasManifest;
    public bool ShowSetup => _isLoaded && !_hasManifest;
    public bool ShowProject => _isLoaded && _hasManifest;
    public bool CanAct => !_isBusy && _hasManifest && _pending is null;
    public bool HasCheckouts => Checkouts.Count > 0;
    public bool HasPackages => Packages.Count > 0;
    public bool HasMessages => Messages.Count > 0;
    public bool HasPlan => _pending is not null;
    public string PlanHeading => _pending is null ? "" : L.Tr("bpm.plan.heading", _pending.Checkout, BpmService.Short(_pending.To));

    public bool SetupContent { get => _setupContent; set { if (SetField(ref _setupContent, value)) OnPropertyChanged(nameof(SetupGame)); } }
    public bool SetupGame { get => !_setupContent; set => SetupContent = !value; }
    public string SetupPackages { get => _setupPackages; set => SetField(ref _setupPackages, value); }
    public string SetupAt { get => _setupAt; set => SetField(ref _setupAt, value); }

    public void ReportProblem(string text)
    {
        Messages.Add(new BpmMessageItem(BpmIssueLevel.Error, text, true));
        OnPropertyChanged(nameof(HasMessages));
    }

    public async Task LoadAsync()
    {
        IsBusy = true;
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Reading {BpmStore.ManifestFileName} of {ProjectName}", ex);
            Messages.Add(new BpmMessageItem(BpmIssueLevel.Error, ex.Message));
        }
        finally { IsBusy = false; }
    }

    private async Task ReloadAsync()
    {
        _hasManifest = BpmStore.Exists(ProjectPath);
        BasisDevRoot = BpmService.BasisDevRoot(ProjectPath);
        Checkouts.Clear();
        Packages.Clear();
        if (_hasManifest)
        {
            var report = await Task.Run(() => _bpm.StatusAsync(ProjectPath));
            var manifest = BpmStore.ReadManifest(ProjectPath);
            var locked = BpmStore.ReadLock(ProjectPath);
            var root = BpmService.BasisDevRoot(ProjectPath);
            var overrides = BpmStore.LocalProject(BpmStore.ReadLocal(root), root, ProjectPath).Overrides ?? new Dictionary<string, string>();
            foreach (var state in report.Checkouts) Checkouts.Add(new BpmCheckoutItem(this, state));
            var mounts = report.Mounts.ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);
            var ids = manifest.Packages.Keys.Concat(locked.Packages.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
            foreach (var id in ids)
                Packages.Add(new BpmPackageItem(this, id, manifest.Packages.GetValueOrDefault(id), locked.Packages.GetValueOrDefault(id), mounts.GetValueOrDefault(id), overrides.GetValueOrDefault(id)));
            ReplaceMessages(report.Issues.Select(i => new BpmMessageItem(i.Level, i.Message)), keepRun: true);
        }
        _isLoaded = true;
        RaiseState();
    }

    private void ReplaceMessages(IEnumerable<BpmMessageItem> items, bool keepRun)
    {
        var run = keepRun ? Messages.Where(m => m.FromRun).ToList() : new List<BpmMessageItem>();
        Messages.Clear();
        foreach (var item in run.Concat(items)) Messages.Add(item);
        OnPropertyChanged(nameof(HasMessages));
    }

    private async Task RunAsync(string label, Func<Action<BpmResult>, Task> steps)
    {
        if (_isBusy) return;
        IsBusy = true;
        Progress = label;
        Messages.Clear();
        var failed = false;
        void Log(BpmResult result)
        {
            foreach (var line in result.Done.Where(d => d.Length > 0)) Messages.Add(new BpmMessageItem(BpmIssueLevel.Info, line, true, true));
            foreach (var issue in result.Issues) Messages.Add(new BpmMessageItem(issue.Level, issue.Message, true));
            failed |= !result.Ok;
            OnPropertyChanged(nameof(HasMessages));
        }
        try
        {
            await steps(r => Avalonia.Threading.Dispatcher.UIThread.Post(() => Log(r)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"{label} {ProjectName}", ex);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Messages.Add(new BpmMessageItem(BpmIssueLevel.Error, ex.Message, true)));
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { });
            Progress = failed ? L.Tr("bpm.status.failed") : "";
            try { await ReloadAsync(); }
            catch (Exception ex) { DiagnosticLog.Write($"Reading {BpmStore.ManifestFileName} of {ProjectName}", ex); }
            IsBusy = false;
            try { await _changed(); }
            catch (Exception ex) { DiagnosticLog.Write("Refreshing the package list", ex); }
        }
    }

    private Action<string> ProgressLine => line => Avalonia.Threading.Dispatcher.UIThread.Post(() => Progress = line);

    private async Task<bool> LockStepsAsync(Action<BpmResult> log)
    {
        var result = await Task.Run(() => _bpm.ResolveAsync(ProjectPath, _catalog(), false, ProgressLine));
        log(result);
        return result.Ok;
    }

    private async Task RestoreStepsAsync(Action<BpmResult> log)
    {
        var checkouts = await Task.Run(() => _bpm.RestoreAsync(ProjectPath, ProgressLine));
        log(checkouts);
        var clones = await _restoreClones(ProgressLine);
        if (clones is not null) log(new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, "clones", clones) }, Array.Empty<string>()));
        log(await _bpm.MountAsync(ProjectPath));
    }

    private async Task RelockAsync(Action<BpmResult> log, bool restore)
    {
        if (!await LockStepsAsync(log)) return;
        if (restore)
        {
            var restored = await Task.Run(() => _bpm.RestoreAsync(ProjectPath, ProgressLine));
            log(restored);
            if (!restored.Ok) return;
        }
        log(await _bpm.MountAsync(ProjectPath));
    }

    private async Task SetupStepsAsync(Action<BpmResult> log)
    {
        var packages = _setupPackages.Split(new[] { ',', ' ', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var at = string.IsNullOrWhiteSpace(_setupAt) ? null : _setupAt.Trim();
        log(await Task.Run(() => _bpm.InitAsync(ProjectPath, _setupContent, BpmService.BasisUrl, at, packages.Length == 0 ? null : packages, copyTemplate: false, ProgressLine)));
    }

    internal Task SetEnabledAsync(BpmPackageItem item, bool enabled) => RunAsync(L.Tr("bpm.status.locking"), async log =>
    {
        BpmService.SetEnabled(ProjectPath, item.Id, enabled);
        await RelockAsync(log, enabled);
    });

    internal Task EmbedAsync(BpmPackageItem item) => RunAsync(L.Tr("bpm.status.embedding", item.Id), async log =>
    {
        var embedded = BpmService.Embed(ProjectPath, item.Id);
        log(embedded);
        if (embedded.Ok) await RelockAsync(log, true);
    });

    internal Task SetOverrideAsync(BpmPackageItem item, string? folder) => RunAsync(L.Tr("bpm.status.mounting"), async log =>
    {
        _bpm.SetOverride(ProjectPath, item.Id, folder);
        log(new BpmResult(true, Array.Empty<BpmIssue>(), new[] { folder is null ? L.Tr("bpm.override.cleared", item.Id) : L.Tr("bpm.override.set", item.Id, folder) }));
        log(await _bpm.MountAsync(ProjectPath));
    });

    internal Task PinAsync(BpmCheckoutItem item, string rev) => RunAsync(L.Tr("bpm.status.pinning"), async log =>
    {
        rev = rev.Trim();
        if (!GitUrlPolicy.IsSafeRef(rev)) { log(new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, rev, L.Tr("bpm.pin.badRef", rev)) }, Array.Empty<string>())); return; }
        var before = BpmStore.ReadManifest(ProjectPath).Checkouts?.GetValueOrDefault(item.Name)?.Commit;
        var moved = await Task.Run(() => _bpm.MovePinAsync(ProjectPath, item.Name, rev, ProgressLine));
        log(moved);
        if (!moved.Ok) return;
        var after = BpmStore.ReadManifest(ProjectPath).Checkouts![item.Name].Commit!;
        var plan = await Task.Run(() => _bpm.PlanTemplateAsync(ProjectPath, item.Name, after));
        if (!plan.IsEmpty)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _pending = new PendingPin(item.Name, before, after, plan);
                PlanChanges.Clear();
                foreach (var change in plan.Changes) PlanChanges.Add(change);
                RaiseState();
            });
            return;
        }
        await RelockAsync(log, true);
    });

    private async Task ApplyPendingAsync(Action<BpmResult> log)
    {
        if (_pending is not { } pending) return;
        ClearPending();
        if (!await LockStepsAsync(log)) return;
        var restored = await Task.Run(() => _bpm.RestoreAsync(ProjectPath, ProgressLine));
        log(restored);
        if (!restored.Ok) return;
        var manifest = BpmStore.ReadManifest(ProjectPath);
        var checkout = manifest.Checkouts![pending.Checkout];
        var folder = BpmService.CheckoutFolder(ProjectPath, pending.Checkout, checkout);
        var assets = BpmService.TemplateFolders(checkout)[0];
        log(BpmService.ApplyTemplate(ProjectPath, pending.Plan, folder, assets.Contains('/') ? assets[..assets.LastIndexOf('/')] : ""));
        log(await _bpm.MountAsync(ProjectPath));
    }

    private void CancelPending()
    {
        if (_pending is not { } pending) return;
        try
        {
            BpmStore.Update(ProjectPath, (manifest, _) => manifest.Checkouts![pending.Checkout].Commit = pending.Before);
            Messages.Add(new BpmMessageItem(BpmIssueLevel.Info, L.Tr("bpm.plan.cancelled", pending.Checkout, BpmService.Short(pending.Before)), true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Messages.Add(new BpmMessageItem(BpmIssueLevel.Error, ex.Message, true)); }
        ClearPending();
        _ = LoadAsync();
    }

    private void ClearPending()
    {
        _pending = null;
        PlanChanges.Clear();
        RaiseState();
    }

    private void RaiseState()
    {
        foreach (var name in new[] { nameof(HasManifest), nameof(ShowSetup), nameof(ShowProject), nameof(CanAct), nameof(HasCheckouts), nameof(HasPackages), nameof(HasMessages), nameof(HasPlan), nameof(PlanHeading), nameof(BasisDevText) })
            OnPropertyChanged(name);
        foreach (var command in new[] { RefreshCommand, LockCommand, RestoreCommand, MountCommand, SetupCommand, ApplyPlanCommand, CancelPlanCommand, OpenBasisDevCommand }) command.Raise();
        foreach (var checkout in Checkouts) checkout.Raise();
        foreach (var package in Packages) package.Raise();
    }

    private sealed record PendingPin(string Checkout, string? Before, string To, BpmTemplatePlan Plan);
}

public sealed class BpmCheckoutItem : ObservableObject
{
    private readonly ProjectFilesViewModel _owner;
    private string _pinTarget = "";

    public BpmCheckoutItem(ProjectFilesViewModel owner, BpmCheckoutState state)
    {
        _owner = owner;
        State = state;
        PinCommand = new RelayCommand(() => _owner.PinAsync(this, _pinTarget), () => _owner.CanAct && IsPinned && _pinTarget.Trim().Length > 0);
        OpenFolderCommand = new RelayCommand(() => ExternalLink.OpenFolder(State.Folder), () => State.Exists);
    }

    public BpmCheckoutState State { get; }
    public string Name => State.Name;
    public bool IsPinned => State.Held == BpmHeld.Pinned;
    public string HeldText => L.Tr(State.Held switch { BpmHeld.Pinned => "bpm.held.pinned", BpmHeld.WorkingTree => "bpm.held.workingTree", _ => "bpm.held.inline" });
    public string WhereText => !State.Exists ? L.Tr("bpm.checkout.missing")
        : State.Branch is { } branch ? L.Tr("bpm.checkout.onBranch", branch, BpmService.Short(State.Head)) : BpmService.Short(State.Head);
    public string LockedText => State.Locked is null ? "" : L.Tr("bpm.checkout.locked", BpmService.Short(State.Locked));
    public bool HasChanges => State.Changes > 0;
    public string ChangesText => L.Tr("bpm.checkout.changes", State.Changes);
    public string Folder => State.Folder;
    public string PinTarget { get => _pinTarget; set { if (SetField(ref _pinTarget, value)) PinCommand.Raise(); } }
    public RelayCommand PinCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public void Raise()
    {
        PinCommand.Raise();
        OpenFolderCommand.Raise();
    }
}

public sealed class BpmPackageItem : ObservableObject
{
    private readonly ProjectFilesViewModel _owner;
    private readonly BpmManifestPackage? _listed;
    private readonly BpmLockPackage? _locked;
    private readonly BpmMountLine? _mount;

    public BpmPackageItem(ProjectFilesViewModel owner, string id, BpmManifestPackage? listed, BpmLockPackage? locked, BpmMountLine? mount, string? overridePath)
    {
        _owner = owner;
        Id = id;
        _listed = listed;
        _locked = locked;
        _mount = mount;
        OverridePath = overridePath;
        ToggleCommand = new RelayCommand(() => _owner.SetEnabledAsync(this, !IsEnabled), () => _owner.CanAct && _listed is not null);
        EmbedCommand = new RelayCommand(() => _owner.EmbedAsync(this), () => _owner.CanAct && CanEmbed);
        ClearOverrideCommand = new RelayCommand(() => _owner.SetOverrideAsync(this, null), () => _owner.CanAct && HasOverride);
    }

    public string Id { get; }
    public string? OverridePath { get; }
    public bool HasOverride => OverridePath is not null;
    public bool IsListed => _listed is not null;
    public bool IsEnabled => _listed?.IsEnabled ?? true;
    public string ToggleText => L.Tr(IsEnabled ? "bpm.package.turnOff" : "bpm.package.turnOn");
    public string Requested => _listed?.Version ?? L.Tr("bpm.package.dependency", _locked?.Depth ?? 1);
    public string LockedText => _locked is null ? L.Tr(IsEnabled ? "bpm.package.notLocked" : "bpm.package.off") : _locked.PackageVersion is { } v ? L.Tr("bpm.package.lockedVersion", v) : "";
    public string MountText => HasOverride ? L.Tr("bpm.package.override", OverridePath!) : _mount is null ? "" : _mount.Line ?? L.Tr("bpm.package.embedded");
    public bool HasCopiedFrom => !string.IsNullOrWhiteSpace(_listed?.CopiedFrom);
    public string CopiedFromText => L.Tr("bpm.package.copiedFrom", _listed?.CopiedFrom ?? "");
    public bool CanEmbed => _locked is not null && BpmVersion.Parse(_locked.Version) is { Kind: BpmVersionKind.Checkout } or { Kind: BpmVersionKind.Git } && _mount?.Line is not null && !HasOverride;
    public bool CanOverride => _locked is not null;
    public RelayCommand ToggleCommand { get; }
    public RelayCommand EmbedCommand { get; }
    public RelayCommand ClearOverrideCommand { get; }

    internal Task SetOverrideAsync(string folder) => _owner.SetOverrideAsync(this, folder);

    public void Raise()
    {
        ToggleCommand.Raise();
        EmbedCommand.Raise();
        ClearOverrideCommand.Raise();
        OnPropertyChanged(nameof(CanSetOverrideNow));
    }

    public bool CanSetOverrideNow => _owner.CanAct && CanOverride;
}

public sealed record BpmMessageItem(BpmIssueLevel Level, string Text, bool FromRun = false, bool Done = false)
{
    public bool IsError => Level == BpmIssueLevel.Error;
    public bool IsWarning => Level == BpmIssueLevel.Warning;
    public bool IsInfo => Level == BpmIssueLevel.Info && !Done;
    public bool IsDone => Done;
}
