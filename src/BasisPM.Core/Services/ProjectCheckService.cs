using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class ProjectCheckRequest
{
    public required string UnityProjectPath { get; init; }
    public string? RepoRoot { get; init; }
    public string? UnityVersion { get; init; }
    public IReadOnlyList<InstalledEditor> Editors { get; init; } = Array.Empty<InstalledEditor>();
    public IReadOnlyCollection<string>? Focus { get; init; }
    public string? Reason { get; init; }
    public bool Compile { get; init; } = true;
    public CompileCheckMode Mode { get; init; } = CompileCheckMode.Auto;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan OpenEditorWait { get; init; } = TimeSpan.FromMinutes(10);
    public IReadOnlySet<string>? Installable { get; init; }
    public bool UpgradeApis { get; init; }
    public bool Save { get; init; } = true;
}

public sealed class ProjectCheckService
{
    private readonly PackageAnalyzer _analyzer;
    private readonly UnityCompileCheckService _compiler;
    private readonly PackageCheckStore _store;
    private readonly BasisUpdateService? _basis;
    private readonly GitService? _git;
    private readonly Func<string, CancellationToken, Task<bool>>? _hasLocalWork;

    public ProjectCheckService(PackageAnalyzer analyzer, UnityCompileCheckService compiler, PackageCheckStore store, BasisUpdateService? basis = null, GitService? git = null,
        Func<string, CancellationToken, Task<bool>>? hasLocalWork = null)
    {
        _analyzer = analyzer;
        _compiler = compiler;
        _store = store;
        _basis = basis;
        _git = git;
        _hasLocalWork = hasLocalWork;
    }

    public PackageCheckStore Store => _store;

    public async Task<ProjectCheckReport> RunAsync(ProjectCheckRequest request, Action<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var project = Path.GetFullPath(request.UnityProjectPath);
        var onDisk = UnityProjectService.ReadUnityVersion(project);
        var unity = onDisk != "unknown" || string.IsNullOrWhiteSpace(request.UnityVersion) ? onDisk : request.UnityVersion!;
        var baseline = _store.Load(project)?.Last;
        var focus = request.Focus?.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        progress?.Invoke("Finding the Basis version this project is on…");
        var basis = await ResolveBasisAsync(request.RepoRoot ?? project, ct).ConfigureAwait(false);
        if (basis is not null) basis.Unity = unity;

        progress?.Invoke("Looking for conflicts between packages…");
        var editor = UnityCompileCheckService.EditorExecutable(UnityCompileCheckService.FindEditor(request.Editors, unity)?.Path);
        var inventory = await _analyzer.ScanProjectAsync(project, EditorFolder(editor), ct).ConfigureAwait(false);
        var issues = _analyzer.Analyze(inventory, new PackageAnalyzerOptions { Focus = focus, Installable = request.Installable }).ToList();

        CompileCheckResult? compile = null;
        List<AppliedApiUpgrade>? upgrades = null;
        if (request.Compile)
        {
            var compileRequest = new CompileCheckRequest
            {
                UnityProjectPath = project,
                UnityVersion = unity,
                Editors = request.Editors,
                Mode = request.Mode,
                Timeout = request.Timeout,
                OpenEditorWait = request.OpenEditorWait,
            };
            compile = await _compiler.RunAsync(compileRequest, progress, ct).ConfigureAwait(false);
            if (request.UpgradeApis && compile.Status == CompileCheckStatus.Failed)
                (compile, upgrades) = await UpgradeApisAsync(compileRequest, compile, project, unity, progress, ct).ConfigureAwait(false);
            issues.AddRange(ToIssues(compile, focus));
        }

        var report = new ProjectCheckReport
        {
            Project = project,
            Reason = request.Reason,
            Focus = focus ?? new List<string>(),
            Started = started,
            Finished = DateTimeOffset.UtcNow,
            UnityVersion = unity,
            Basis = basis,
            Issues = Dedupe(issues),
            Compile = compile,
            Uncompiled = PackageCheckVerdicts.Uncompiled(inventory, compile),
            Upgrades = upgrades,
            Tool = BasisDevStore.ToolName,
        };
        report.Introduced = Introduced(report, baseline);
        report.Attributed = PackageCheckVerdicts.Attribute(report, inventory);
        foreach (var package in PackagesToRecord(inventory, focus))
            report.Packages[package.Id] = PackageCheckVerdicts.StateFor(report, package.Id, BasisPackagesStore.ReadVersion(package.Root), await CommitOfAsync(project, package, ct).ConfigureAwait(false));
        if (request.Save) _store.Save(report);
        return report;
    }

    private async Task<(CompileCheckResult Compile, List<AppliedApiUpgrade>? Upgrades)> UpgradeApisAsync(CompileCheckRequest request, CompileCheckResult first, string project, string unity,
        Action<string>? progress, CancellationToken ct)
    {
        var planned = new List<ApiUpgradeEdit>();
        foreach (var clone in ApiUpgradeService.Plan(first.Diagnostics, project).GroupBy(e => e.CloneFolder, StringComparer.OrdinalIgnoreCase))
            if (_hasLocalWork is null || !await _hasLocalWork(clone.Key, ct).ConfigureAwait(false)) planned.AddRange(clone);
        if (planned.Count == 0) return (first, null);
        var applied = ApiUpgradeService.Apply(planned, unity);
        if (applied.Count == 0) return (first, null);
        progress?.Invoke(applied.Count == 1 ? "Upgraded 1 obsolete Unity API call. Compiling again…" : $"Upgraded {applied.Count} obsolete Unity API calls. Compiling again…");
        CompileCheckResult again;
        try { again = await _compiler.RunAsync(request, progress, ct).ConfigureAwait(false); }
        catch
        {
            RevertAll(applied);
            throw;
        }
        if (again.Status is not (CompileCheckStatus.Passed or CompileCheckStatus.Failed))
        {
            RevertAll(applied);
            return (first, null);
        }
        var kept = ApiUpgradeService.KeepWorking(applied, project, again.Diagnostics);
        var comparer = Platform.PathComparison() == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var reverted = applied.Where(e => !kept.Any(k => k.CloneFolder == e.CloneFolder && k.File == e.File))
            .Select(e => Path.GetFullPath(Path.Combine(e.CloneFolder, e.File))).ToHashSet(comparer);
        var result = reverted.Count == 0 ? again : Merge(again, first, project, reverted);
        return (result, kept.Count == 0 ? null : kept.Select(e => new AppliedApiUpgrade(e.Package, e.File, e.Line, e.From, e.To)).ToList());
    }

    private static void RevertAll(IEnumerable<ApiUpgradeEdit> applied)
    {
        foreach (var (clone, file) in applied.Select(e => (e.CloneFolder, e.File)).Distinct()) ApiUpgradeService.Revert(clone, file);
    }

    private static CompileCheckResult Merge(CompileCheckResult again, CompileCheckResult first, string project, IReadOnlySet<string> reverted)
    {
        bool InReverted(CompileDiagnostic d) => d.File is not null && reverted.Contains(Path.GetFullPath(Path.IsPathRooted(d.File) ? d.File : Path.Combine(project, d.File)));
        var diagnostics = again.Diagnostics.Where(d => !InReverted(d)).Concat(first.Diagnostics.Where(InReverted)).ToList();
        var failed = again.FailedAssemblies.Union(first.Diagnostics.Where(InReverted).Select(d => d.Assembly).OfType<string>(), StringComparer.Ordinal).ToList();
        var status = diagnostics.Any(d => d.Severity == IssueSeverity.Error) ? CompileCheckStatus.Failed : again.Status;
        return new CompileCheckResult
        {
            Status = status,
            Mode = again.Mode,
            Detail = status == again.Status ? again.Detail : first.Detail,
            Diagnostics = diagnostics,
            FailedAssemblies = failed,
            LogPath = again.LogPath,
            UnityVersion = again.UnityVersion,
            EditorPath = again.EditorPath,
            ExitCode = again.ExitCode,
            Started = first.Started,
            Duration = first.Duration + again.Duration,
        };
    }

    public static IEnumerable<PackageIssue> ToIssues(CompileCheckResult compile, IReadOnlyCollection<string>? focus)
    {
        foreach (var d in compile.Diagnostics)
        {
            if (d.Severity == IssueSeverity.Info || d.Severity == IssueSeverity.Warning && (focus is null || d.Package is null || !focus.Contains(d.Package, StringComparer.OrdinalIgnoreCase))) continue;
            var code = d.Kind == CompileDiagnosticKind.PackageResolution ? IssueCodes.PackageResolution
                : d.Severity == IssueSeverity.Error ? IssueCodes.CompileError : IssueCodes.CompileWarning;
            yield return new PackageIssue(code, d.Severity, d.Package ?? UnityPackageMap.AssetsId, string.IsNullOrEmpty(d.Code) ? d.Message : $"{d.Code}: {d.Message}")
            {
                File = d.File,
                Line = d.Line,
                Column = d.Column,
                Other = d.Assembly,
            };
        }
    }

    public static List<string> Introduced(ProjectCheckReport report, ProjectCheckReport? baseline)
    {
        if (baseline is null) return new List<string>();
        var before = baseline.Issues.Select(PackageCheckKeys.Of).ToHashSet(StringComparer.Ordinal);
        var compiledBefore = baseline.Compile?.Status is CompileCheckStatus.Passed or CompileCheckStatus.Failed or CompileCheckStatus.PackagesFailed;
        bool Focused(PackageIssue issue) => baseline.Focus.Count == 0 || baseline.Focus.Contains(issue.Package, StringComparer.OrdinalIgnoreCase);
        bool Covered(PackageIssue issue) => issue.Code is IssueCodes.CompileError or IssueCodes.CompileWarning or IssueCodes.PackageResolution
            ? compiledBefore && (issue.Severity == IssueSeverity.Error || Focused(issue))
            : Focused(issue);
        return report.Issues.Where(Covered).Select(PackageCheckKeys.Of).Where(k => !before.Contains(k)).Distinct(StringComparer.Ordinal).ToList();
    }

    private static List<PackageIssue> Dedupe(IEnumerable<PackageIssue> issues) =>
        issues.GroupBy(PackageCheckKeys.Of, StringComparer.Ordinal).Select(g => g.First())
            .OrderByDescending(i => i.Severity).ThenBy(i => i.Package, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Line).ToList();

    private static IEnumerable<InspectedPackage> PackagesToRecord(ProjectInventory inventory, IReadOnlyCollection<string>? focus) =>
        inventory.Packages.Where(p => p.Origin != PackageOrigin.Project && !p.Id.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)
            && (focus is null || focus.Contains(p.Id, StringComparer.OrdinalIgnoreCase)));

    private async Task<BasisCheckTarget?> ResolveBasisAsync(string repoRoot, CancellationToken ct)
    {
        if (_basis is null) return null;
        try
        {
            var info = await _basis.ResolveBasisBaseAsync(repoRoot, ct).ConfigureAwait(false);
            if (info is null) return null;
            var root = await _basis.ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
            DateTimeOffset? date = null;
            if (_git is not null && root is not null) date = (await _git.GetLogAsync(root, info.Sha, 1, ct).ConfigureAwait(false)).FirstOrDefault()?.Date;
            return new BasisCheckTarget { Branch = info.Branch, Commit = info.Sha, Date = date, Source = info.Source.ToString().ToLowerInvariant() };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Finding the Basis version of {repoRoot} for a package check", ex);
            return null;
        }
    }

    private async Task<string?> CommitOfAsync(string project, InspectedPackage package, CancellationToken ct)
    {
        var listed = BasisPackagesStore.Find(project, package.Id)?.Commit;
        if (_git is null || package.Origin is PackageOrigin.Cache or PackageOrigin.Project) return listed;
        var clone = FindClone(project, package.Root);
        return clone is null ? listed : await _git.ResolveCommitAsync(clone, "HEAD", ct).ConfigureAwait(false) ?? listed;
    }

    private static string? FindClone(string project, string root)
    {
        var stop = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project));
        for (var dir = new DirectoryInfo(root); dir is not null && !Platform.PathsEqual(Path.TrimEndingDirectorySeparator(dir.FullName), stop); dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        return null;
    }

    private static string? EditorFolder(string? editorExecutable)
    {
        if (string.IsNullOrWhiteSpace(editorExecutable)) return null;
        if (Directory.Exists(editorExecutable)) return editorExecutable.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? Path.Combine(editorExecutable, "Contents") : editorExecutable;
        return Path.GetDirectoryName(editorExecutable);
    }
}
