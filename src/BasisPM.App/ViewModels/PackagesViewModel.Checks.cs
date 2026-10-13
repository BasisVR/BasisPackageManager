using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private ProjectCheckService? _checks;
    private readonly RegistryChecksService _registryChecksService = new();
    private RegistryChecksFile? _registryChecks;
    private string? _projectBasisCommit;
    private string? _projectBasisBranch;
    private ProjectCheckHistory? _checkHistory;
    private string? _checkHistoryProject;
    private readonly Dictionary<string, (BasisInstall Target, HashSet<string> Ids)> _installedThisRun = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _verifyingProjects = new(StringComparer.OrdinalIgnoreCase);
    private RelayCommand? _verifyProjectCommand;
    private RelayCommand<PackageRow>? _verifyPackageCommand;
    private RelayCommand<PackageRow>? _openCheckLogCommand;

    private ProjectCheckService Checks => _checks ??= new ProjectCheckService(new PackageAnalyzer(), new UnityCompileCheckService(), new PackageCheckStore(), _basisUpdates, _gitService, _mountService.HasLocalWorkAsync);

    public RelayCommand VerifyProjectCommand => _verifyProjectCommand ??= new RelayCommand(
        () => _install is { } target ? VerifyAsync(target, null, "manual") : Task.CompletedTask,
        () => _install is { HasUnityProject: true } target && !_verifyingProjects.Contains(target.RepoRoot));

    public RelayCommand<PackageRow> VerifyPackageCommand => _verifyPackageCommand ??= new RelayCommand<PackageRow>(
        row => _install is { } target && row is not null ? VerifyAsync(target, new[] { row.Name }, "manual") : Task.CompletedTask,
        row => row is not null && _install is { HasUnityProject: true } target && !_verifyingProjects.Contains(target.RepoRoot));

    public RelayCommand<PackageRow> OpenCheckLogCommand => _openCheckLogCommand ??= new RelayCommand<PackageRow>(row => ExternalLink.OpenFile(row?.CheckLogPath), row => ExternalLink.CanOpenFile(row?.CheckLogPath));

    public bool IsVerifying => _install is { } target && _verifyingProjects.Contains(target.RepoRoot);

    private async Task<IReadOnlyList<PackageIssue>> AnalyzeCandidateAsync(BasisInstall target, string packageRoot, string packageId)
    {
        if (!target.HasUnityProject) return Array.Empty<PackageIssue>();
        try
        {
            var (editors, installable) = await Dispatcher.UIThread.InvokeAsync(() => (_shell.UnityVM.InstalledEditors.ToList(), _catalogService.AllLatest(_catalog).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)));
            var editor = UnityCompileCheckService.FindEditor(editors, target.UnityVersion);
            return await Task.Run(async () =>
            {
                var analyzer = new PackageAnalyzer();
                var inventory = await analyzer.ScanProjectAsync(target.UnityProjectPath, EditorFolderOf(editor)).ConfigureAwait(false);
                var candidate = analyzer.InspectPackage(new ProjectPackageRef(packageId, packageRoot, PackageOrigin.Local));
                return analyzer.Analyze(inventory.With(candidate), new PackageAnalyzerOptions { Focus = new[] { packageId }, Installable = installable });
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Checking {packageId} against {target.DisplayName} before installing", ex);
            return Array.Empty<PackageIssue>();
        }
    }

    private void NoteInstalled(BasisInstall target, CatalogPackageVersion entry)
    {
        if (!target.HasUnityProject || !target.Manifest.Dependencies.ContainsKey(entry.Name)) return;
        if (!_installedThisRun.TryGetValue(target.RepoRoot, out var run)) _installedThisRun[target.RepoRoot] = run = (target, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        run.Ids.Add(entry.Name);
    }

    private async void StartVerifyingInstalls()
    {
        if (_installedThisRun.Count == 0) return;
        var runs = _installedThisRun.Values.ToList();
        _installedThisRun.Clear();
        try
        {
            if (!(await _settingsService.LoadAsync()).VerifyAfterInstall) return;
            foreach (var (target, ids) in runs) await VerifyAsync(target, ids.ToList(), "install");
        }
        catch (Exception ex) { DiagnosticLog.Write("Checking freshly installed packages", ex); }
    }

    public async void VerifyAfterBasisChange(string repoRoot)
    {
        try
        {
            if (!(await _settingsService.LoadAsync()).VerifyAfterInstall) return;
            if (InstallOptions.FirstOrDefault(i => Platform.PathsEqual(i.RepoRoot, repoRoot)) is { HasUnityProject: true } target) await VerifyAsync(target, null, "basis-update");
        }
        catch (Exception ex) { DiagnosticLog.Write($"Checking packages after a Basis update of {repoRoot}", ex); }
    }

    public async Task VerifyAsync(BasisInstall target, IReadOnlyCollection<string>? ids, string reason)
    {
        if (!target.HasUnityProject || !_verifyingProjects.Add(target.RepoRoot)) return;
        RaiseVerifying();
        var names = ids is { Count: > 0 } ? string.Join(", ", ids.Select(DisplayNameOf)) : null;
        var activity = _shell.BeginActivity(names is null ? L.Tr("packages.verify.activityAll", target.DisplayName) : L.Tr("packages.verify.activity", names), target.DisplayName);
        var key = "verify|" + target.RepoRoot;
        try
        {
            var open = UnityProjectService.IsOpenInUnity(target.UnityProjectPath);
            if (open) _shell.ReportActivity(activity, L.Tr("packages.verify.waitingForUnity", target.DisplayName));
            var request = new ProjectCheckRequest
            {
                UnityProjectPath = target.UnityProjectPath,
                RepoRoot = target.RepoRoot,
                UnityVersion = target.UnityVersion,
                Editors = await EditorsAsync(target),
                Focus = ids,
                Reason = reason,
                UpgradeApis = true,
            };
            void Progress(string line) => Dispatcher.UIThread.Post(() => _shell.ReportActivity(activity, line));
            Task<ProjectCheckReport> Run() => Checks.RunAsync(request, Progress);
            var report = open ? await Run() : await _shell.Work.RunAsync(target.RepoRoot, L.Tr("packages.verify.activityAll", target.DisplayName), Run,
                running => _shell.ReportActivity(activity, L.Tr("shell.work.waiting", running)));
            ReportVerification(target, report, ids, reason, key);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Checking packages in {target.DisplayName}", ex);
            SetStatus(L.Tr("packages.verify.error", ex.Message), StatusKind.Error, target.DisplayName, () => VerifyAsync(target, ids, reason), key);
        }
        finally
        {
            _shell.EndActivity(activity);
            _verifyingProjects.Remove(target.RepoRoot);
            RaiseVerifying();
            if (_install is { } active && Platform.PathsEqual(active.RepoRoot, target.RepoRoot))
            {
                CheckHistoryFor(active, reload: true);
                ApplyCheckState();
            }
        }
    }

    private void ReportVerification(BasisInstall target, ProjectCheckReport report, IReadOnlyCollection<string>? ids, string reason, string key)
    {
        var focus = ids is { Count: > 0 } ? ids : null;
        var names = focus is null ? null : string.Join(", ", focus.Select(DisplayNameOf));
        bool Blamed(PackageIssue issue) => focus is null || focus.Contains(issue.Package, StringComparer.OrdinalIgnoreCase)
            || report.IsIntroduced(issue) && (report.Attributed is null || focus.Any(id => report.Attributed.GetValueOrDefault(id)?.Contains(PackageCheckKeys.Of(issue)) == true));
        var errors = report.Issues.Where(i => i.Severity == IssueSeverity.Error && Blamed(i)).ToList();
        Func<Task> retry = () => VerifyAsync(target, ids, reason);
        if (errors.Count > 0)
        {
            var where = IssueLine(errors[0]);
            SetStatus(names is null
                    ? errors.Count == 1 ? L.Tr("packages.verify.failedAllOne", target.DisplayName, where) : L.Tr("packages.verify.failedAll", target.DisplayName, errors.Count, where)
                    : errors.Count == 1 ? L.Tr("packages.verify.failedOne", names, where) : L.Tr("packages.verify.failed", names, errors.Count, where),
                StatusKind.Error, target.DisplayName, retry, key);
            return;
        }
        var broken = report.Issues.Where(i => i.Severity == IssueSeverity.Error).ToList();
        switch (report.Compile?.Status)
        {
            case CompileCheckStatus.Passed:
                SetStatus(names is null ? L.Tr("packages.verify.passedAll", target.DisplayName) : L.Tr("packages.verify.passed", names, target.DisplayName), StatusKind.Success, target.DisplayName, key: key);
                break;
            case CompileCheckStatus.Failed when broken.Count > 0:
                var others = string.Join(", ", broken.Select(i => i.Package).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => p == UnityPackageMap.AssetsId ? L.Tr("packages.verify.projectScripts") : DisplayNameOf(p)));
                if (focus is not null && focus.All(id => report.Packages.GetValueOrDefault(id) is { Compiled: true, Verdict: PackageVerdict.Works or PackageVerdict.Warnings }))
                    SetStatus(L.Tr("packages.verify.passedDespite", names, target.DisplayName, others, IssueLine(broken[0])), StatusKind.Info, target.DisplayName, key: key);
                else
                    SetStatus(L.Tr("packages.verify.blockedBy", names ?? target.DisplayName, others, IssueLine(broken[0])), StatusKind.Error, target.DisplayName, retry, key);
                break;
            case CompileCheckStatus.EditorMissing:
                SetStatus(L.Tr("packages.verify.noEditor", target.UnityVersion), StatusKind.Info, target.DisplayName, key: key);
                break;
            case CompileCheckStatus.Waiting:
                SetStatus(L.Tr("packages.verify.unityIdle", target.DisplayName), StatusKind.Info, target.DisplayName, retry, key);
                break;
            case null:
                SetStatus(L.Tr("packages.verify.staticOnly", target.DisplayName), StatusKind.Success, target.DisplayName, key: key);
                break;
            default:
                SetStatus(L.Tr("packages.verify.notCompiled", report.Compile.Detail ?? report.Compile.Status.ToString()), StatusKind.Error, target.DisplayName, retry, key);
                break;
        }
    }

    private async Task<IReadOnlyList<InstalledEditor>> EditorsAsync(BasisInstall target)
    {
        var editors = _shell.UnityVM.InstalledEditors.ToList();
        editors.AddRange(UnityEditorLocator.FindInstalled(UnityProjectService.ReadUnityVersion(target.UnityProjectPath)));
        if (UnityCompileCheckService.FindEditor(editors, UnityProjectService.ReadUnityVersion(target.UnityProjectPath)) is not null) return editors;
        var settings = await _settingsService.LoadAsync();
        try { editors.AddRange(await new UnityHubService().ListInstalledAsync(settings.UnityHubPath)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write("Listing Unity editors for a package check", ex); }
        foreach (var manual in settings.ManualEditors)
            if (!editors.Any(e => string.Equals(e.Version, manual.Version, StringComparison.OrdinalIgnoreCase))) editors.Add(manual.ToInstalledEditor());
        return editors;
    }

    private async Task RefreshCheckInfoAsync(BasisInstall? target)
    {
        _registryChecks ??= await _registryChecksService.TryLoadAsync(_officialCatalogUrl);
        _projectBasisCommit = null;
        _projectBasisBranch = null;
        _checkHistory = null;
        if (target is { IsGitRepo: true })
        {
            try
            {
                var basis = await _basisUpdates.ResolveBasisBaseAsync(target.RepoRoot);
                _projectBasisCommit = basis?.Sha;
                _projectBasisBranch = basis?.Branch;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write($"Finding the Basis version of {target.DisplayName} for package checks", ex); }
        }
        ApplyCheckState();
    }

    private void ApplyCheckState()
    {
        var target = _install;
        var history = target is { HasUnityProject: true } ? CheckHistoryFor(target) : null;
        foreach (var row in Available)
        {
            var local = history?.Packages.GetValueOrDefault(row.Name);
            var registry = RegistryChecksService.Find(_registryChecks, row.Name, _projectBasisBranch);
            var head = RegistryChecksService.Head(_registryChecks, _projectBasisBranch)?.Commit;
            var commit = target is null ? null : BasisPackagesStore.Find(target.UnityProjectPath, row.Name)?.Commit;
            row.ApplyCheck(PackageCheckVerdicts.Summarize(local, registry, _projectBasisCommit, commit, head), _projectBasisBranch,
                history?.Last is { } last ? last.IssuesFor(row.Name).ToList() : Array.Empty<PackageIssue>(), history?.Last?.Compile?.LogPath, row.IsInstalled || row.IsMounted);
        }
    }

    private ProjectCheckHistory? CheckHistoryFor(BasisInstall target, bool reload = false)
    {
        if (reload || _checkHistory is null || !Platform.PathsEqual(_checkHistoryProject, target.UnityProjectPath))
        {
            _checkHistory = Checks.Store.Load(target.UnityProjectPath);
            _checkHistoryProject = target.UnityProjectPath;
        }
        return _checkHistory;
    }

    private void RaiseVerifying()
    {
        OnPropertyChanged(nameof(IsVerifying));
        _verifyProjectCommand?.Raise();
        _verifyPackageCommand?.Raise();
    }

    private static string IssueLine(PackageIssue issue) => string.IsNullOrEmpty(issue.Location) ? issue.Message : $"{issue.Location}: {issue.Message}";

    private string DisplayNameOf(string id) => _catalogService.AllLatest(_catalog).FirstOrDefault(e => string.Equals(e.Name, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;

    private static string? EditorFolderOf(InstalledEditor? editor) =>
        UnityCompileCheckService.EditorExecutable(editor?.Path) is { } exe ? Path.GetDirectoryName(exe) : null;
}

public sealed partial class PackageRow
{
    private PackageCheckSummary? _check;
    private string? _checkBranch;
    private IReadOnlyList<PackageIssue> _checkIssues = Array.Empty<PackageIssue>();
    private IReadOnlyList<PackageCheckIssueItem> _checkIssueItems = Array.Empty<PackageCheckIssueItem>();
    private string? _checkLogPath;
    private bool _checkApplies;

    public void ApplyCheck(PackageCheckSummary? check, string? branch, IReadOnlyList<PackageIssue> issues, string? logPath, bool inProject)
    {
        _check = check;
        _checkBranch = branch;
        _checkIssues = check is { FromProject: true } ? issues.OrderByDescending(i => i.Severity).ToList() : Array.Empty<PackageIssue>();
        _checkIssueItems = _checkIssues.Take(MaxShownIssues).Select(i => new PackageCheckIssueItem(i.Message, i.Location, i.Fix, i.Severity == IssueSeverity.Error, i.Severity == IssueSeverity.Warning)).ToList();
        _checkLogPath = check is { FromProject: true } ? logPath : null;
        _checkApplies = check is not null && (check.FromProject ? inProject : true);
        foreach (var name in new[] { nameof(HasCheck), nameof(CheckLabel), nameof(CheckTip), nameof(CheckWorks), nameof(CheckWarns), nameof(CheckBreaks), nameof(CheckUnknown),
            nameof(CheckIssues), nameof(CheckIssueItems), nameof(HasCheckIssues), nameof(HasMoreCheckIssues), nameof(MoreCheckIssuesText), nameof(CheckLogPath), nameof(HasCheckLog), nameof(CheckDetail) })
            OnPropertyChanged(name);
    }

    public bool HasCheck => _checkApplies;
    public bool CheckWorks => _checkApplies && _check!.Verdict == PackageVerdict.Works && !_check.Stale;
    public bool CheckWarns => _checkApplies && (_check!.Verdict == PackageVerdict.Warnings || _check.Stale && _check.Verdict != PackageVerdict.Breaks);
    public bool CheckBreaks => _checkApplies && _check!.Verdict == PackageVerdict.Breaks;
    public bool CheckUnknown => _checkApplies && _check!.Verdict == PackageVerdict.Unknown && !_check.Stale;
    public const int MaxShownIssues = 40;
    public IReadOnlyList<PackageIssue> CheckIssues => _checkIssues;
    public IReadOnlyList<PackageCheckIssueItem> CheckIssueItems => _checkIssueItems;
    public bool HasMoreCheckIssues => _checkIssues.Count > MaxShownIssues;
    public string MoreCheckIssuesText => L.Tr("packages.check.moreIssues", _checkIssues.Count - MaxShownIssues);
    public bool HasCheckIssues => _checkIssues.Count > 0;
    public string? CheckLogPath => _checkLogPath;
    public bool HasCheckLog => !string.IsNullOrWhiteSpace(_checkLogPath) && File.Exists(_checkLogPath);

    public string CheckLabel => !_checkApplies ? "" : _check!.Verdict switch
    {
        PackageVerdict.Breaks => L.Tr(_check.FromProject ? "packages.check.breaksHere" : "packages.check.breaksOn", BasisChannels.Describe(_checkBranch)),
        PackageVerdict.Warnings => L.Tr("packages.check.warnings", _check.State.Warnings),
        PackageVerdict.Works when _check.Stale => L.Tr("packages.check.stale"),
        PackageVerdict.Works => L.Tr(_check.FromProject ? "packages.check.worksHere" : "packages.check.worksOn", BasisChannels.Describe(_checkBranch)),
        _ => L.Tr("packages.check.notCompiled"),
    };

    public string CheckTip => Describe(withSummary: true);

    public string CheckDetail => Describe(withSummary: _checkIssues.Count == 0);

    private string Describe(bool withSummary) => !_checkApplies ? "" : string.Join(" ", new[]
    {
        L.Tr(_check!.FromProject ? "packages.check.tip.project" : "packages.check.tip.registry", BasisChannels.Describe(_check.State.BasisBranch ?? _checkBranch),
            _check.State.Checked is { } when ? when.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "?"),
        withSummary && _check.State.Summary is { Length: > 0 } summary ? L.Tr("packages.check.tip.first", summary) : null,
        _check.Stale ? L.Tr(_check.FromProject ? "packages.check.tip.staleProject" : "packages.check.tip.staleRegistry") : null,
    }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public sealed record PackageCheckIssueItem(string Message, string Location, string? Fix, bool IsError, bool IsWarning)
{
    public bool IsInfo => !IsError && !IsWarning;
    public bool HasLocation => Location.Length > 0;
    public bool HasFix => !string.IsNullOrWhiteSpace(Fix);
}
