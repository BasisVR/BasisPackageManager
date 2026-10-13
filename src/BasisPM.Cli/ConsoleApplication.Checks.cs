using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private ProjectCheckService? _checks;

    private ProjectCheckService Checks => _checks ??= new ProjectCheckService(new PackageAnalyzer(), new UnityCompileCheckService(), new PackageCheckStore(Path.Combine(_stateDirectory, PackageCheckStore.FolderName)), _updates, _git, _mounts.HasLocalWorkAsync);

    private async Task VerifyAsync(List<string> options)
    {
        var noCompile = TakeFlag(options, "--no-compile");
        var upgradeApis = TakeFlag(options, "--upgrade-apis");
        var batch = TakeFlag(options, "--batch");
        var openEditor = TakeFlag(options, "--open-editor");
        var timeout = TakeInt(options, "--timeout", 1, 600);
        var record = TakeValue(options, "--record");
        if (batch && openEditor) throw new UsageException("Pick one of --batch and --open-editor.");
        var install = await LoadInstallAsync();
        if (!install.HasUnityProject) throw new InvalidOperationException($"{install.DisplayName} has no Unity project to check.");
        var focus = options.Count == 0 ? null : options.Select(o => CanonicalPackageId(install, o)).ToList();
        var editors = await EditorsForAsync(await _settings.LoadAsync(), install.UnityProjectPath, askHub: !noCompile);
        ProjectCheckReport? report = null;
        await RunOperationAsync(async ct =>
        {
            report = await Checks.RunAsync(new ProjectCheckRequest
            {
                UnityProjectPath = install.UnityProjectPath,
                RepoRoot = install.RepoRoot,
                UnityVersion = install.UnityVersion,
                Editors = editors,
                Focus = focus,
                Reason = focus is null ? "verify" : "verify " + string.Join(", ", focus),
                Compile = !noCompile,
                UpgradeApis = upgradeApis,
                Mode = batch ? CompileCheckMode.Batch : openEditor ? CompileCheckMode.OpenEditor : CompileCheckMode.Auto,
                Timeout = TimeSpan.FromMinutes(timeout ?? 60),
            }, line => { if (!Out.JsonMode) Out.Progress(line); }, ct);
        }, "Checking " + install.DisplayName + Out.Ellipsis);
        if (report is null) return;
        if (record is not null) RecordRegistryChecks(FullPath(record), report, focus);
        if (report.HasErrors || (!noCompile && report.Compile?.Status != CompileCheckStatus.Passed)) _exitCode = 1;
        if (Out.JsonMode)
        {
            Out.Json(report);
            return;
        }
        WriteCheckReport(install, report, focus);
    }

    private async Task CheckPackageAsync(List<string> options)
    {
        Expect(options, 1, 1, "a package folder");
        var folder = FullPath(options[0]);
        if (!File.Exists(Path.Combine(folder, "package.json"))) throw new InvalidOperationException($"{folder} has no package.json, so Unity can't load it as a package.");
        var install = await TryLoadInstallAsync();
        IReadOnlyList<PackageIssue> issues = Array.Empty<PackageIssue>();
        var analyzer = new PackageAnalyzer();
        var id = UnityPackageMap.ReadName(folder) ?? Path.GetFileName(folder);
        await RunOperationAsync(async ct =>
        {
            issues = install is { HasUnityProject: true }
                ? await analyzer.CheckPackageAsync(install.UnityProjectPath, folder, id, null, ct)
                : analyzer.Analyze(new ProjectInventory { UnityProjectPath = folder }.With(analyzer.InspectPackage(new ProjectPackageRef(id, folder, PackageOrigin.Local), ct)), new PackageAnalyzerOptions { Focus = new[] { id } });
        }, "Checking " + id + Out.Ellipsis);
        if (issues.Any(i => i.Severity == IssueSeverity.Error)) _exitCode = 1;
        if (Out.JsonMode)
        {
            Out.Json(new { package = id, folder, project = install?.DisplayName, issues });
            return;
        }
        Out.Say(new Span(id, Tone.Bold), new Span(install is { HasUnityProject: true } ? $"  checked against {install.DisplayName}" : "  checked on its own", Tone.Dim));
        if (issues.Count == 0) Out.Success("No problems found.");
        else WriteIssues(issues, folder);
        WriteIssueTotals(issues);
    }

    private async Task CheckRegistryAsync(List<string> options)
    {
        var record = TakeValue(options, "--record");
        var noCompile = TakeFlag(options, "--no-compile");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var install = await LoadInstallAsync();
        if (!install.HasUnityProject) throw new InvalidOperationException($"{install.DisplayName} has no Unity project to check.");
        var catalog = await LoadCatalogAsync();
        var shipped = await LoadShippedPackagesAsync(install);
        var latest = _catalogs.AllLatest(catalog).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var entries = options.Count == 0 ? latest
            : options.Select(id => latest.FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new UsageException(NotFound(id, catalog, install))).ToList();
        if (!assumeYes && !Confirm($"This installs {Plural(entries.Count, "registry package")} into {install.DisplayName} one at a time, checks each one, and puts the project's package files back afterwards. Use a spare copy of Basis. Go ahead?")) return;
        var editors = await EditorsForAsync(await _settings.LoadAsync(), install.UnityProjectPath, askHub: !noCompile);
        var snapshot = CheckSnapshot.Take(install);
        var mountsBefore = _mounts.ListMounts(install.UnityProjectPath).Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var results = new List<(CatalogPackageVersion Entry, PackageCheckState? State, string? Problem)>();
        foreach (var entry in entries)
        {
            Out.Heading(entry.DisplayName + "  " + entry.Name);
            var present = shipped.IsIncluded(entry.Name) || HasDependency(install, entry.Name);
            try
            {
                if (!present) await InstallCatalogPackageAsync(install, catalog, entry, null, true, shipped);
                install = await _installs.LoadAsync(install.RepoRoot, install.Alias);
                ProjectCheckReport? report = null;
                await RunOperationAsync(async ct => report = await Checks.RunAsync(new ProjectCheckRequest
                {
                    UnityProjectPath = install.UnityProjectPath,
                    RepoRoot = install.RepoRoot,
                    UnityVersion = install.UnityVersion,
                    Editors = editors,
                    Focus = new[] { entry.Name },
                    Reason = "registry " + entry.Name,
                    Compile = !noCompile,
                    Mode = CompileCheckMode.Batch,
                    Save = false,
                }, Out.Progress, ct), "Checking " + entry.Name + Out.Ellipsis);
                var state = report!.Packages.GetValueOrDefault(entry.Name) ?? PackageCheckVerdicts.StateFor(report, entry.Name, entry.Version);
                if (record is not null && report.Basis is { Branch.Length: > 0 }) RecordRegistryChecks(FullPath(record), report, new[] { entry.Name });
                results.Add((entry, state, report.Compile is { Ran: false } compile ? compile.Detail ?? compile.Status.ToString() : null));
                Out.Say(new Span("  "), VerdictSpan(state));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write($"Checking registry package {entry.Name}", ex);
                Out.Error($"{entry.DisplayName}: {ex.Message}");
                results.Add((entry, null, ex.Message));
            }
            finally
            {
                if (!present)
                {
                    foreach (var mount in _mounts.ListMounts(install.UnityProjectPath).Where(m => !mountsBefore.Contains(m.PackageId)))
                        _mounts.Forget(install, mount.PackageId, mount.FolderPath);
                    snapshot.Restore();
                    install = await _installs.LoadAsync(install.RepoRoot, install.Alias);
                }
            }
        }
        Out.Say();
        Out.Table(results.Select(r => new[] { new Span(r.Entry.Name), r.State is null ? new Span("not checked: " + r.Problem, Tone.Warn) : VerdictSpan(r.State) }).ToList(), indent: 0);
        if (results.Any(r => r.State is null)) _exitCode = 1;
    }

    private sealed class CheckSnapshot
    {
        private readonly Dictionary<string, byte[]?> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _folders = new(StringComparer.OrdinalIgnoreCase);

        public static CheckSnapshot Take(BasisInstall install)
        {
            var snapshot = new CheckSnapshot();
            var packages = Path.Combine(install.UnityProjectPath, "Packages");
            foreach (var file in new[] { Path.Combine(packages, "manifest.json"), Path.Combine(packages, "packages-lock.json"), BasisPackagesStore.FilePath(install.UnityProjectPath),
                ServerPackageService.ManifestPath(install.RepoRoot), ServerPackageService.LockPath(install.RepoRoot), ServerPackageService.LocalPath(install.RepoRoot) })
                snapshot._files[file] = File.Exists(file) ? File.ReadAllBytes(file) : null;
            foreach (var folder in new[] { BasisDevStore.Root(install.UnityProjectPath), ServerPackageService.PackagesDirectory(install.RepoRoot), packages })
                snapshot._folders[folder] = Directory.Exists(folder) ? Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }

        public void Restore()
        {
            foreach (var (folder, before) in _folders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder).ToList())
                {
                    if (before.Contains(Path.GetFileName(entry))) continue;
                    try
                    {
                        if (File.Exists(entry)) File.Delete(entry);
                        else
                        {
                            foreach (var file in Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                            Directory.Delete(entry, recursive: true);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Removing {entry} after a registry check", ex); Out.Warning($"Couldn't remove {entry}: {ex.Message}"); }
                }
            }
            foreach (var (file, content) in _files)
            {
                try
                {
                    if (content is null) { if (File.Exists(file)) File.Delete(file); }
                    else if (!File.Exists(file) || !File.ReadAllBytes(file).AsSpan().SequenceEqual(content)) File.WriteAllBytes(file, content);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Restoring {file} after a registry check", ex); Out.Warning($"Couldn't restore {file}: {ex.Message}"); }
            }
        }
    }

    private async Task<List<InstalledEditor>> EditorsForAsync(UserSettings settings, string unityProjectPath, bool askHub)
    {
        var version = UnityProjectService.ReadUnityVersion(unityProjectPath);
        var editors = settings.ManualEditors.Where(m => !string.IsNullOrWhiteSpace(m.Path)).Select(m => m.ToInstalledEditor()).ToList();
        editors.AddRange(UnityEditorLocator.FindInstalled(version));
        return UnityCompileCheckService.FindEditor(editors, version) is not null || !askHub ? editors : await ListAllEditorsAsync(settings);
    }

    private void WriteCheckReport(BasisInstall install, ProjectCheckReport report, IReadOnlyCollection<string>? focus)
    {
        var basis = report.Basis is { } b ? $"Basis {BasisChannels.Describe(b.Branch)}" + (b.Commit is null ? "" : " " + Short(b.Commit)) + (b.Date is { } date ? $" ({date:yyyy-MM-dd})" : "") : "Basis version unknown";
        Out.Say(new Span(install.DisplayName, Tone.Bold), new Span($"  {basis}, Unity {report.UnityVersion}", Tone.Dim));
        if (report.Compile is { } compile) WriteCompileLine(install, compile);
        else Out.Note("Not compiled (--no-compile).");
        foreach (var group in (report.Upgrades ?? new List<AppliedApiUpgrade>()).GroupBy(u => u.Package, StringComparer.OrdinalIgnoreCase))
            Out.Success($"Upgraded {Plural(group.Count(), "obsolete API call")} in {group.Key} for Unity {report.UnityVersion}: " + string.Join(", ", group.Select(u => $"{u.From} {Out.Arrow} {u.To}").Distinct()) + ".");
        var shown = report.Issues.Where(i => focus is null || focus.Contains(i.Package, StringComparer.OrdinalIgnoreCase)
            || i.Severity == IssueSeverity.Error && (report.IsIntroduced(i) || i.Code is IssueCodes.CompileError or IssueCodes.PackageResolution)).ToList();
        if (shown.Count > 0)
        {
            Out.Say();
            WriteIssues(shown, install.UnityProjectPath, report);
        }
        var states = report.Packages.Where(p => focus is null ? p.Value.Verdict is PackageVerdict.Breaks or PackageVerdict.Warnings : true).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (states.Count > 0)
        {
            Out.Say();
            var rows = states.Select(p => new[] { new Span(p.Key), VerdictSpan(p.Value) }).ToList();
            Out.Table(rows, indent: 0);
        }
        Out.Say();
        WriteIssueTotals(shown);
        if (report.Introduced.Count > 0) Out.Warning(report.Introduced.Count == 1 ? "1 problem wasn't there at the last check." : $"{report.Introduced.Count:N0} problems weren't there at the last check.");
        if (report.Compile?.LogPath is { } log) Out.Hint("Unity's log: " + log);
    }

    private void WriteCompileLine(BasisInstall install, CompileCheckResult compile)
    {
        var took = compile.Duration.TotalSeconds >= 1 ? $" in {FormatDuration(compile.Duration)}" : "";
        var how = compile.Mode == CompileCheckMode.OpenEditor ? " (read from the open Unity editor)" : "";
        switch (compile.Status)
        {
            case CompileCheckStatus.Passed: Out.Success($"Compiles{took}{how}."); break;
            case CompileCheckStatus.Failed: Out.Say(new Span(Out.Cross + " ", Tone.Bad), new Span($"Doesn't compile: {Plural(compile.ErrorCount, "error")}{took}{how}.")); break;
            case CompileCheckStatus.PackagesFailed: Out.Say(new Span(Out.Cross + " ", Tone.Bad), new Span($"Unity couldn't load the packages{took}, so nothing was compiled.")); break;
            case CompileCheckStatus.EditorMissing: Out.Warning($"Not compiled: Unity {install.UnityVersion} isn't installed. '{Prefix}unity install {install.UnityVersion}' gets it."); break;
            case CompileCheckStatus.ProjectOpen: Out.Warning("Not compiled: another Unity editor has the project open. Close it, or run again with --open-editor."); break;
            case CompileCheckStatus.Waiting: Out.Warning("Not compiled yet: Unity has the project open but hasn't recompiled. Switch to Unity, then run the check again."); break;
            default: Out.Warning($"Not compiled: {compile.Detail ?? compile.Status.ToString()}"); break;
        }
    }

    private void WriteIssues(IReadOnlyList<PackageIssue> issues, string root, ProjectCheckReport? report = null)
    {
        foreach (var group in issues.GroupBy(i => i.Package, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(i => i.Severity)).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            Out.Heading(group.Key == UnityPackageMap.AssetsId ? "Your project's own scripts" : group.Key);
            foreach (var issue in group.OrderByDescending(i => i.Severity).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Line))
            {
                var (mark, tone) = issue.Severity switch { IssueSeverity.Error => (Out.Cross, Tone.Bad), IssueSeverity.Warning => ("!", Tone.Warn), _ => (Out.Dot, Tone.Dim) };
                var fresh = report is not null && report.IsIntroduced(issue) ? " (new)" : "";
                Out.Say(new Span("  " + mark + " ", tone), new Span(issue.Message + fresh));
                if (issue.Location.Length > 0) Out.Say(new Span("    " + RelativeLocation(root, issue), Tone.Dim));
                if (!string.IsNullOrWhiteSpace(issue.Fix)) Out.Say(new Span("    " + Out.Arrow + " " + issue.Fix, Tone.Dim));
            }
        }
    }

    private static void WriteIssueTotals(IReadOnlyCollection<PackageIssue> issues)
    {
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        if (errors == 0 && warnings == 0) return;
        Out.Say(new Span(Plural(errors, "error"), errors > 0 ? Tone.Bad : Tone.Dim), new Span(", "), new Span(Plural(warnings, "warning"), warnings > 0 ? Tone.Warn : Tone.Dim));
    }

    private static Span VerdictSpan(PackageCheckState state) => state.Verdict switch
    {
        PackageVerdict.Works => new Span("works", Tone.Good),
        PackageVerdict.Warnings => new Span(Plural(state.Warnings, "warning"), Tone.Warn),
        PackageVerdict.Breaks => new Span(state.Summary is { Length: > 0 } summary ? "breaks: " + Out.Truncate(summary, Math.Max(30, Out.Width - 40)) : "breaks", Tone.Bad),
        _ => new Span(state.Compiled ? "not compiled" : "not compiled yet", Tone.Dim),
    };

    private static string RelativeLocation(string root, PackageIssue issue)
    {
        var file = issue.File ?? "";
        if (Path.IsPathRooted(file))
        {
            var relative = Path.GetRelativePath(root, file);
            if (!relative.StartsWith("..", StringComparison.Ordinal)) file = relative;
        }
        file = file.Replace('\\', '/');
        return issue.Line is null ? file : issue.Column is null ? $"{file}:{issue.Line}" : $"{file}:{issue.Line}:{issue.Column}";
    }

    private static string FormatDuration(TimeSpan span) => span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" : $"{span.TotalSeconds:0}s";

    private static string CanonicalPackageId(BasisInstall install, string id)
    {
        var known = UnityPackageMap.List(install.UnityProjectPath, includeAssets: false).Select(p => p.Id).Concat(install.Manifest.Dependencies.Keys);
        return known.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? throw new UsageException($"{id} isn't in {install.DisplayName}." + (Suggest(id, known) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : ""));
    }

    private static void RecordRegistryChecks(string path, ProjectCheckReport report, IReadOnlyCollection<string>? focus)
    {
        if (report.Basis is not { Branch.Length: > 0 } basis) throw new InvalidOperationException("Couldn't tell which Basis branch this project is on, so the result can't be recorded.");
        var file = File.Exists(path) ? RegistryChecksService.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException($"{path} isn't a checks file.") : new RegistryChecksFile();
        foreach (var (id, state) in report.Packages.Where(p => focus is null || focus.Contains(p.Key, StringComparer.OrdinalIgnoreCase)))
            RegistryChecksService.Set(file, id, basis.Branch, state, basis);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, RegistryChecksService.Serialize(file));
        Out.Hint($"Recorded the results in {path}");
    }
}
