using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisDevService
{
    private const int HistorySearchDepth = 2000;
    private const int PackageSearchDepth = 4;
    private static readonly string[] SkippedFolders = { "Library", "Temp", "Logs", "obj", "bin", "node_modules" };
    private static readonly Regex CommitSha = new("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant);

    private readonly GitService _git;
    private readonly UnityProjectService _projects;
    private readonly MountRegistry _registry;
    private readonly MountService _mounts;

    public BasisDevService(GitService git, UnityProjectService projects, MountRegistry registry, MountService mounts)
    {
        _git = git;
        _projects = projects;
        _registry = registry;
        _mounts = mounts;
    }

    public BasisDevReport Scan(BasisInstall install) => Classify(install, install.Manifest.Dependencies, null);

    public async Task<BasisDevReport> ScanAsync(BasisInstall install, bool compare = true, CancellationToken ct = default)
    {
        var manifest = await LoadDependenciesAsync(install, ct).ConfigureAwait(false);
        var tracked = await TrackedPackageFoldersAsync(install, ct).ConfigureAwait(false);
        var report = Classify(install, manifest, tracked);
        if (!_git.IsAvailable) return report;

        var packages = new List<BasisDevPackage>(report.Packages.Count);
        foreach (var package in report.Packages)
        {
            ct.ThrowIfCancellationRequested();
            var current = package;
            if (current.CloneExists && current.PackageRoot is null)
                current = current with { PackageRoot = FindPackageRoot(current.CloneFolder!, current.Id, current.Sidecar?.Upstream.Path ?? UpmGitUrl.Parse(current.RecordOriginal)?.Path, null, deep: true) };
            if (current.CloneExists && current.Sidecar is null)
                current = current with { Proposed = await ProposeAsync(install, current, ct).ConfigureAwait(false) };
            if (compare && (current.Issues.Contains(BasisDevIssueKind.Shadowed) || current.HandledByProject))
                current = current with { Comparison = await CompareAsync(install, current, ct).ConfigureAwait(false) };
            packages.Add(current);
        }
        return report with { Packages = packages };
    }

    public async Task<BasisDevResult> ApplyAsync(BasisInstall install, string packageId, BasisDevAction action, bool force = false, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var package = (await ScanAsync(install, compare: false, ct).ConfigureAwait(false)).Find(packageId);
        if (package is null) return BasisDevResult.Fail($"{packageId} isn't part of this project.");
        if (!package.Actions.Contains(action) && !(action == BasisDevAction.Release && package.CloneExists) && !(action == BasisDevAction.Unignore && package.Sidecar?.HandledByProject == true))
            return BasisDevResult.Fail($"{Describe(action)} doesn't apply to {package.Id} right now.");
        try
        {
            return action switch
            {
                BasisDevAction.Record => Record(install, package),
                BasisDevAction.Prune => Prune(install, package),
                BasisDevAction.UseClone => await UseCloneAsync(install, package, force, ct).ConfigureAwait(false),
                BasisDevAction.Release => await ReleaseAsync(install, package, force, ct).ConfigureAwait(false),
                BasisDevAction.Restore => await RestoreAsync(install, package, ct).ConfigureAwait(false),
                BasisDevAction.Reclone => await RecloneAsync(install, package, onProgress, ct).ConfigureAwait(false),
                BasisDevAction.Ignore => MarkHandledByProject(install, package, true),
                BasisDevAction.Unignore => MarkHandledByProject(install, package, false),
                _ => BasisDevResult.Fail($"{Describe(action)} isn't supported."),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            DiagnosticLog.Write($"Applying {action} to {package.Id}", ex);
            return BasisDevResult.Fail($"{Describe(action)} failed for {package.Id}: {ex.Message}");
        }
    }

    public async Task<BasisDevResult> ReconcileAsync(BasisInstall install, CancellationToken ct = default)
    {
        var report = await ScanAsync(install, compare: false, ct).ConfigureAwait(false);
        int recorded = 0, pruned = 0;
        var problems = new List<string>();
        foreach (var package in report.Packages)
        {
            var record = package.Actions.Contains(BasisDevAction.Record);
            if (!record && !package.Actions.Contains(BasisDevAction.Prune)) continue;
            BasisDevResult result;
            try { result = record ? Record(install, package) : Prune(install, package); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                DiagnosticLog.Write($"Reconciling {package.Id}", ex);
                result = BasisDevResult.Fail($"{package.Id}: {ex.Message}");
            }
            if (!result.Ok) problems.Add(result.Message);
            else if (record) recorded++;
            else pruned++;
        }
        var summary = $"Recorded {recorded} clone(s) and forgot {pruned} stale mount record(s).";
        return problems.Count == 0 ? BasisDevResult.Success(summary) : BasisDevResult.Fail(summary + " " + string.Join(" ", problems));
    }

    public static string Describe(BasisDevAction action) => action switch
    {
        BasisDevAction.Record => "Record",
        BasisDevAction.Prune => "Forget",
        BasisDevAction.UseClone => "Use clone",
        BasisDevAction.Release => "Release clone",
        BasisDevAction.Restore => "Restore",
        BasisDevAction.Reclone => "Clone again",
        BasisDevAction.Ignore => "Ignore",
        BasisDevAction.Unignore => "Unignore",
        _ => action.ToString(),
    };

    private BasisDevReport Classify(BasisInstall install, IReadOnlyDictionary<string, string> manifest, IReadOnlySet<string>? trackedFolders)
    {
        var project = install.UnityProjectPath;
        var embedded = new Dictionary<string, LocalPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in _projects.ListEmbeddedPackages(project)) embedded.TryAdd(package.Id, package);
        var sidecars = new Dictionary<string, BasisDevSidecar>(StringComparer.OrdinalIgnoreCase);
        foreach (var sidecar in BasisDevStore.List(project)) sidecars.TryAdd(sidecar.Package, sidecar);
        var records = new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _registry.ForInstall(project)) records[record.PackageId] = record;
        var clones = BasisDevStore.ListClones(project);

        var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(manifest.Keys);
        ids.UnionWith(embedded.Keys);
        ids.UnionWith(sidecars.Keys);
        ids.UnionWith(records.Keys);
        ids.UnionWith(clones.Keys);

        var packages = new List<BasisDevPackage>(ids.Count);
        foreach (var id in ids)
        {
            manifest.TryGetValue(id, out var value);
            embedded.TryGetValue(id, out var local);
            sidecars.TryGetValue(id, out var sidecar);
            records.TryGetValue(id, out var record);
            clones.TryGetValue(id, out var looseClone);
            packages.Add(Classify(project, id, value, local, sidecar, record, looseClone, trackedFolders));
        }
        return new BasisDevReport(project, packages);
    }

    private static BasisDevPackage Classify(string project, string id, string? value, LocalPackage? local, BasisDevSidecar? sidecar, MountRecord? record, string? looseClone, IReadOnlySet<string>? trackedFolders)
    {
        var candidates = new[] { sidecar is null ? null : BasisDevStore.ResolveFolder(project, sidecar), record?.FolderPath, looseClone };
        var cloneFolder = candidates.FirstOrDefault(MountService.IsWorkingClone) ?? candidates.FirstOrDefault(c => c is not null);
        if (cloneFolder is null && local is not null && trackedFolders is not null && !trackedFolders.Contains(local.FolderName) && MountService.IsWorkingClone(local.FolderPath))
            cloneFolder = local.FolderPath;

        var cloneExists = MountService.IsWorkingClone(cloneFolder);
        var embeddedIsClone = cloneExists && local is not null && SamePath(local.FolderPath, cloneFolder!);
        var target = LocalTarget(project, value);
        var pointsAtClone = cloneFolder is not null && target is not null && IsInside(target, cloneFolder);
        var cloneActive = cloneExists && (embeddedIsClone || (local is null && pointsAtClone));
        var tracked = local is not null && !embeddedIsClone && (trackedFolders?.Contains(local.FolderName) ?? !MountService.IsWorkingClone(local.FolderPath));

        var source = local is not null
            ? embeddedIsClone ? PackageSourceKind.DevClone : tracked ? PackageSourceKind.ProjectRepo : PackageSourceKind.LocalFolder
            : SourceOf(value, pointsAtClone);

        var issues = new List<BasisDevIssueKind>();
        if (cloneExists)
        {
            if (!cloneActive && sidecar?.HandledByProject != true) issues.Add(local is not null ? BasisDevIssueKind.Shadowed : BasisDevIssueKind.Detached);
            if (sidecar is null) issues.Add(BasisDevIssueKind.Unrecorded);
        }
        else if ((sidecar is not null || record is not null) && cloneFolder is not null)
        {
            var vanished = local is null && value is null && SamePath(cloneFolder, Path.Combine(project, "Packages", id));
            issues.Add(pointsAtClone || vanished ? BasisDevIssueKind.MissingClone : BasisDevIssueKind.StaleRecord);
        }

        return new BasisDevPackage
        {
            Id = id,
            Source = source,
            ManifestValue = value,
            EmbeddedFolder = local?.FolderPath,
            EmbeddedTracked = tracked,
            Sidecar = sidecar,
            RecordFolder = record?.FolderPath,
            RecordOriginal = string.IsNullOrWhiteSpace(record?.OriginalManifestValue) ? null : record.OriginalManifestValue,
            CloneFolder = cloneFolder,
            CloneExists = cloneExists,
            CloneActive = cloneActive,
            PackageRoot = cloneExists ? FindPackageRoot(cloneFolder!, id, sidecar?.Upstream.Path ?? UpmGitUrl.Parse(record?.OriginalManifestValue)?.Path, pointsAtClone ? target : null, deep: false) : null,
            Issues = issues,
        };
    }

    private static PackageSourceKind SourceOf(string? value, bool pointsAtClone)
    {
        if (string.IsNullOrWhiteSpace(value)) return PackageSourceKind.None;
        if (pointsAtClone) return PackageSourceKind.DevClone;
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return IsTarball(value) ? PackageSourceKind.Tarball : PackageSourceKind.LocalPath;
        return UpmGitUrl.Parse(value) is not null ? PackageSourceKind.Git : PackageSourceKind.Registry;
    }

    private BasisDevResult Record(BasisInstall install, BasisDevPackage package)
    {
        if (package.Proposed is null) return BasisDevResult.Fail($"Couldn't work out where {package.Id} comes from, so it wasn't recorded.");
        package.Proposed.Recorded = DateTimeOffset.UtcNow;
        BasisDevStore.Write(install.UnityProjectPath, package.Proposed);
        ExcludeFromProject(install, package.CloneFolder);
        var upstream = package.Proposed.Upstream;
        return BasisDevResult.Success($"Recorded {package.Id}: {DescribeUpstream(upstream)}.");
    }

    private BasisDevResult MarkHandledByProject(BasisInstall install, BasisDevPackage package, bool handled)
    {
        if (package.CloneFolder is null) return BasisDevResult.Fail($"{package.Id} has no clone.");
        var sidecar = package.Sidecar ?? package.Proposed ?? new BasisDevSidecar
        {
            Package = package.Id,
            Folder = BasisDevStore.RelativeFolder(install.UnityProjectPath, package.CloneFolder),
            Tool = BasisDevStore.ToolName,
        };
        sidecar.HandledByProject = handled ? true : null;
        sidecar.Recorded ??= DateTimeOffset.UtcNow;
        BasisDevStore.Write(install.UnityProjectPath, sidecar);
        ExcludeFromProject(install, package.CloneFolder);
        var folder = RelativeTo(install.UnityProjectPath, package.CloneFolder);
        return BasisDevResult.Success(handled
            ? $"{package.Id} is left to {DescribeSource(package)}; {folder} is kept and no longer flagged."
            : $"{folder} is flagged again while {package.Id} doesn't load from it.");
    }

    private BasisDevResult Prune(BasisInstall install, BasisDevPackage package)
    {
        _mounts.Forget(install, package.Id, package.CloneFolder);
        return BasisDevResult.Success($"Forgot the stale mount record for {package.Id}; {RelativeTo(install.UnityProjectPath, package.CloneFolder)} was left as it is.");
    }

    private async Task<BasisDevResult> UseCloneAsync(BasisInstall install, BasisDevPackage package, bool force, CancellationToken ct)
    {
        if (package.CloneFolder is null || package.PackageRoot is null) return BasisDevResult.Fail($"No package.json for {package.Id} was found in its clone.");
        if (package.EmbeddedFolder is not null) return BasisDevResult.Fail($"Packages/{Path.GetFileName(package.EmbeddedFolder)} takes precedence over the clone. Release one of them first.");
        if (!IsSafeCloneFolder(install.UnityProjectPath, package.CloneFolder)) return BasisDevResult.Fail($"{package.CloneFolder} is outside this project's Packages and {BasisDevStore.FolderName} folders.");

        var line = "file:" + Path.GetRelativePath(Path.Combine(install.UnityProjectPath, "Packages"), package.PackageRoot).Replace('\\', '/');
        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
        info.Manifest.Dependencies.TryGetValue(package.Id, out var previous);
        if (!force && !string.IsNullOrWhiteSpace(previous) && string.Equals(previous, await CommittedLineAsync(install, package.Id, ct).ConfigureAwait(false), StringComparison.Ordinal))
            return BasisDevResult.Fail($"The project has committed {package.Id} as {previous}. Using the clone changes that line in manifest.json until you release the clone, so don't commit manifest.json in the meantime.", needsForce: true);
        info.Manifest.Dependencies[package.Id] = line;
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        install.Manifest = info.Manifest;

        var sidecar = package.Sidecar ?? package.Proposed ?? new BasisDevSidecar
        {
            Package = package.Id,
            Folder = BasisDevStore.RelativeFolder(install.UnityProjectPath, package.CloneFolder),
            Tool = BasisDevStore.ToolName,
        };
        var previousTarget = LocalTarget(install.UnityProjectPath, previous);
        if (!string.IsNullOrWhiteSpace(previous) && (previousTarget is null || !IsInside(previousTarget, package.CloneFolder)))
            sidecar.Manifest.Original = previous;
        sidecar.Manifest.Mounted = line;
        sidecar.HandledByProject = null;
        sidecar.Recorded = DateTimeOffset.UtcNow;
        BasisDevStore.Write(install.UnityProjectPath, sidecar);
        ExcludeFromProject(install, package.CloneFolder);
        return BasisDevResult.Success($"{package.Id} now loads from {RelativeTo(install.UnityProjectPath, package.PackageRoot)}.");
    }

    private async Task<BasisDevResult> ReleaseAsync(BasisInstall install, BasisDevPackage package, bool force, CancellationToken ct)
    {
        if (!package.CloneExists || package.CloneFolder is null) return BasisDevResult.Fail($"{package.Id} has no clone to release.");
        if (!IsSafeCloneFolder(install.UnityProjectPath, package.CloneFolder)) return BasisDevResult.Fail($"{package.CloneFolder} is outside this project's Packages and {BasisDevStore.FolderName} folders, so it was left alone.");
        var folder = RelativeTo(install.UnityProjectPath, package.CloneFolder);
        if (!force && await _mounts.HasLocalWorkAsync(package.CloneFolder, ct).ConfigureAwait(false))
            return BasisDevResult.Fail($"{folder} has uncommitted or unpushed work. Release it anyway to delete that work.", needsForce: true);

        var target = LocalTarget(install.UnityProjectPath, package.ManifestValue);
        var pointsAtClone = target is not null && IsInside(target, package.CloneFolder);
        var shadowed = package.EmbeddedFolder is not null && !SamePath(package.EmbeddedFolder, package.CloneFolder);
        var restore = !shadowed && (package.CloneActive || pointsAtClone);
        var line = restore ? await RestoreLineAsync(install, package, ct).ConfigureAwait(false) : null;
        var result = await _mounts.UnmountAsync(install, package.Id, restore, package.CloneFolder, line, ct).ConfigureAwait(false);
        if (!result.Ok) return BasisDevResult.Fail(result.Error ?? $"Couldn't release {folder}.");

        if (shadowed && pointsAtClone)
        {
            var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
            if (info.Manifest.Dependencies.Remove(package.Id))
            {
                await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
                install.Manifest = info.Manifest;
            }
        }
        return BasisDevResult.Success(restore
            ? $"Released {folder}; {package.Id} is back on {install.Manifest.Dependencies.GetValueOrDefault(package.Id)}."
            : $"Released {folder}; {package.Id} still loads from {DescribeSource(package)}.");
    }

    private async Task<BasisDevResult> RestoreAsync(BasisInstall install, BasisDevPackage package, CancellationToken ct)
    {
        var line = await RestoreLineAsync(install, package, ct).ConfigureAwait(false) ?? package.Upstream?.ManifestUrl;
        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(line)) info.Manifest.Dependencies[package.Id] = line;
        else info.Manifest.Dependencies.Remove(package.Id);
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        install.Manifest = info.Manifest;
        _mounts.Forget(install, package.Id, package.CloneFolder);
        return BasisDevResult.Success(string.IsNullOrWhiteSpace(line)
            ? $"Removed the broken manifest line for {package.Id}; its original source wasn't recorded."
            : $"{package.Id} is back on {line}.");
    }

    private async Task<BasisDevResult> RecloneAsync(BasisInstall install, BasisDevPackage package, Action<string>? onProgress, CancellationToken ct)
    {
        var folder = package.CloneFolder;
        if (folder is null || !IsSafeCloneFolder(install.UnityProjectPath, folder)) return BasisDevResult.Fail($"{package.Id} has no clone folder inside this project.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) return BasisDevResult.Fail($"{RelativeTo(install.UnityProjectPath, folder)} already exists and isn't empty.");
        if (!_git.IsAvailable) return BasisDevResult.Fail("Git was not found on PATH.");

        var original = UpmGitUrl.Parse(package.OriginalManifestValue);
        var url = package.Upstream?.Url is { Length: > 0 } recorded ? recorded : original?.CloneUrl;
        if (string.IsNullOrWhiteSpace(url)) return BasisDevResult.Fail($"The upstream of {package.Id} wasn't recorded, so it can't be cloned again.");
        var gitRef = package.Upstream?.Ref ?? package.Upstream?.Commit ?? original?.Ref;
        var clone = await _git.CloneAtAsync(url, folder, gitRef, onProgress, ct).ConfigureAwait(false);
        if (!clone.Ok)
        {
            try { if (Directory.Exists(folder)) await BasisInstallService.DeleteFolderAsync(folder).ConfigureAwait(false); }
            catch (Exception ex) { DiagnosticLog.Write($"Cleaning up the failed clone {folder}", ex); }
            return BasisDevResult.Fail($"Clone failed: {clone.Output}");
        }

        var sidecar = package.Sidecar ?? new BasisDevSidecar
        {
            Package = package.Id,
            Folder = BasisDevStore.RelativeFolder(install.UnityProjectPath, folder),
            Upstream = new BasisDevUpstream { Url = url, Ref = original?.Ref, Path = original?.Path },
            Manifest = new BasisDevManifestLine { Original = package.OriginalManifestValue, Mounted = package.ManifestValue },
            Tool = BasisDevStore.ToolName,
        };
        sidecar.Upstream.Commit = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
        sidecar.HandledByProject = null;
        sidecar.Recorded = DateTimeOffset.UtcNow;
        BasisDevStore.Write(install.UnityProjectPath, sidecar);
        ExcludeFromProject(install, folder);
        return BasisDevResult.Success($"Cloned {package.Id} again into {RelativeTo(install.UnityProjectPath, folder)}.");
    }

    private async Task<BasisDevSidecar?> ProposeAsync(BasisInstall install, BasisDevPackage package, CancellationToken ct)
    {
        var clone = package.CloneFolder!;
        var relative = BasisDevStore.RelativeFolder(install.UnityProjectPath, clone);
        if (BasisDevStore.ResolveFolder(install.UnityProjectPath, relative) is null) return null;
        try
        {
            var origin = await _git.GetRemoteUrlAsync(clone, "origin", ct).ConfigureAwait(false);
            var original = UpmGitUrl.Parse(package.RecordOriginal);
            var url = UpmGitUrl.Parse(origin)?.CloneUrl ?? (GitUrlPolicy.IsSafeUrl(origin) ? origin!.Trim() : null) ?? original?.CloneUrl ?? "";
            var branch = await _git.GetCurrentBranchAsync(clone, ct).ConfigureAwait(false);
            var gitRef = branch ?? (original?.Ref is { } pinned && !CommitSha.IsMatch(pinned) ? pinned : null);
            var path = package.PackageRoot is { } root ? GitPath(clone, root) : original?.Path;
            var target = LocalTarget(install.UnityProjectPath, package.ManifestValue);
            var pointsAtClone = target is not null && IsInside(target, clone);
            var line = package.RecordOriginal
                ?? (!pointsAtClone && UpmGitUrl.Parse(package.ManifestValue) is not null ? package.ManifestValue : null)
                ?? (UpmGitUrl.Parse(url) is { } parsed ? parsed.ToManifestUrl(gitRef, string.IsNullOrEmpty(path) ? null : path) : null);
            return new BasisDevSidecar
            {
                Package = package.Id,
                Folder = relative,
                Upstream = new BasisDevUpstream
                {
                    Url = url,
                    Ref = gitRef,
                    Path = string.IsNullOrEmpty(path) ? null : path,
                    Commit = await _git.ResolveCommitAsync(clone, "HEAD", ct).ConfigureAwait(false),
                },
                Manifest = new BasisDevManifestLine { Original = line, Mounted = pointsAtClone ? package.ManifestValue : null },
                Tool = BasisDevStore.ToolName,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Reading the upstream of {clone}", ex);
            return null;
        }
    }

    private async Task<BasisDevComparison?> CompareAsync(BasisInstall install, BasisDevPackage package, CancellationToken ct)
    {
        if (!install.IsGitRepo || !package.EmbeddedTracked || package.EmbeddedFolder is null || package.CloneFolder is null || package.PackageRoot is null) return null;
        var embeddedPath = GitPath(install.RepoRoot, package.EmbeddedFolder);
        var clonePath = GitPath(package.CloneFolder, package.PackageRoot);
        if (string.IsNullOrEmpty(embeddedPath) || clonePath is null) return null;
        try
        {
            if (await _git.IsPartialCloneAsync(package.CloneFolder, ct).ConfigureAwait(false)) return null;
            var embeddedTree = await _git.ResolveObjectAsync(install.RepoRoot, "HEAD:" + embeddedPath, ct).ConfigureAwait(false);
            var cloneTree = await _git.ResolveObjectAsync(package.CloneFolder, clonePath.Length == 0 ? "HEAD^{tree}" : "HEAD:" + clonePath, ct).ConfigureAwait(false);
            if (embeddedTree is null || cloneTree is null) return null;
            if (string.Equals(embeddedTree, cloneTree, StringComparison.OrdinalIgnoreCase))
                return new BasisDevComparison(true, 0, 0, 0, await _git.ResolveCommitAsync(package.CloneFolder, "HEAD", ct).ConfigureAwait(false));

            var ours = await _git.ListTreeBlobsAsync(install.RepoRoot, embeddedTree, ct).ConfigureAwait(false);
            var theirs = await _git.ListTreeBlobsAsync(package.CloneFolder, cloneTree, ct).ConfigureAwait(false);
            if (ours is null || theirs is null) return null;
            int added = 0, removed = 0, modified = 0;
            foreach (var (path, blob) in ours)
            {
                if (!theirs.TryGetValue(path, out var other)) added++;
                else if (!string.Equals(blob, other, StringComparison.OrdinalIgnoreCase)) modified++;
            }
            foreach (var path in theirs.Keys)
                if (!ours.ContainsKey(path)) removed++;
            var match = await _git.FindCommitWithTreeAsync(package.CloneFolder, clonePath, embeddedTree, HistorySearchDepth, ct).ConfigureAwait(false);
            return new BasisDevComparison(false, added, removed, modified, match);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Comparing {package.EmbeddedFolder} with {package.CloneFolder}", ex);
            return null;
        }
    }

    private async Task<string?> RestoreLineAsync(BasisInstall install, BasisDevPackage package, CancellationToken ct)
    {
        var original = package.OriginalManifestValue is { Length: > 0 } value && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? value : null;
        var committed = await CommittedLineAsync(install, package.Id, ct).ConfigureAwait(false);
        if (committed is null || original is null || string.Equals(committed, original, StringComparison.Ordinal)) return original ?? committed;
        var gitFolder = package.CloneFolder is null ? null : Path.Combine(package.CloneFolder, ".git");
        var cloned = gitFolder is not null && Directory.Exists(gitFolder) ? new DateTimeOffset(Directory.GetCreationTimeUtc(gitFolder), TimeSpan.Zero) : package.Sidecar?.Recorded;
        var changed = ManifestGitPath(install) is { } path ? await _git.GetLastCommitTimeAsync(install.RepoRoot, path, ct).ConfigureAwait(false) : null;
        return changed is { } committedAt && cloned is { } clonedAt && committedAt > clonedAt ? committed : original;
    }

    private async Task<string?> CommittedLineAsync(BasisInstall install, string packageId, CancellationToken ct)
    {
        if (!install.IsGitRepo || !_git.IsAvailable || ManifestGitPath(install) is not { } path) return null;
        try
        {
            var text = await _git.ShowFileAsync(install.RepoRoot, "HEAD", path, ct).ConfigureAwait(false);
            if (text is null) return null;
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object
                && dependencies.TryGetProperty(packageId, out var line) && line.ValueKind == JsonValueKind.String
                && line.GetString() is { Length: > 0 } committed && !committed.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? committed : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            DiagnosticLog.Write($"Reading the committed manifest line for {packageId}", ex);
            return null;
        }
    }

    private static string? ManifestGitPath(BasisInstall install) =>
        GitPath(install.RepoRoot, Path.Combine(install.UnityProjectPath, "Packages", "manifest.json")) is { Length: > 0 } path ? path : null;

    private async Task<IReadOnlyDictionary<string, string>> LoadDependenciesAsync(BasisInstall install, CancellationToken ct)
    {
        try { return (await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false)).Manifest.Dependencies; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Reading the manifest of {install.UnityProjectPath}", ex);
            return install.Manifest.Dependencies;
        }
    }

    private async Task<IReadOnlySet<string>?> TrackedPackageFoldersAsync(BasisInstall install, CancellationToken ct)
    {
        if (!install.IsGitRepo || !_git.IsAvailable) return null;
        var relative = GitPath(install.RepoRoot, Path.Combine(install.UnityProjectPath, "Packages"));
        if (relative is null) return null;
        try
        {
            var names = await _git.ListTreeNamesAsync(install.RepoRoot, relative.Length == 0 ? "HEAD^{tree}" : "HEAD:" + relative, ct).ConfigureAwait(false);
            return names is null ? null : new HashSet<string>(names, StringComparer.FromComparison(Platform.PathComparison()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Listing the packages tracked by {install.RepoRoot}", ex);
            return null;
        }
    }

    private static string? FindPackageRoot(string clone, string id, string? hintPath, string? manifestTarget, bool deep)
    {
        if (manifestTarget is not null && IsInside(manifestTarget, clone) && File.Exists(Path.Combine(manifestTarget, "package.json"))) return manifestTarget;
        if (BasisDevStore.PackageRoot(clone, hintPath) is { } hinted) return hinted;
        if (File.Exists(Path.Combine(clone, "package.json"))) return clone;
        return deep ? SearchPackageRoot(clone, id) : null;
    }

    private static string? SearchPackageRoot(string clone, string id)
    {
        var pending = new Queue<(string Folder, int Depth)>();
        pending.Enqueue((clone, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Dequeue();
            List<string> children;
            try { children = Directory.EnumerateDirectories(folder).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                var packageJson = Path.Combine(child, "package.json");
                if (File.Exists(packageJson) && string.Equals(ReadPackageName(packageJson), id, StringComparison.OrdinalIgnoreCase)) return child;
                if (depth + 1 < PackageSearchDepth) pending.Enqueue((child, depth + 1));
            }
        }
        return null;
    }

    private static string? ReadPackageName(string packageJson)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(packageJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? LocalTarget(string project, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || IsTarball(value)) return null;
        var path = value[5..].Trim();
        if (path.Length == 0 || path.Any(char.IsControl)) return null;
        try { return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(project, "Packages", path.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private void ExcludeFromProject(BasisInstall install, string? cloneFolder)
    {
        var root = BasisDevStore.Root(install.UnityProjectPath);
        GitExclude.Add(install.RepoRoot, root, _git.GetCommonGitDir);
        if (cloneFolder is not null && !IsInside(cloneFolder, root)) GitExclude.Add(install.RepoRoot, cloneFolder, _git.GetCommonGitDir);
    }

    private static bool IsTarball(string value) =>
        value.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeCloneFolder(string project, string folder) =>
        BasisDevStore.ResolveFolder(project, BasisDevStore.RelativeFolder(project, folder)) is { } resolved && SamePath(resolved, folder);

    private static bool IsInside(string path, string folder)
    {
        var inner = Normalize(path);
        var outer = Normalize(folder);
        return string.Equals(inner, outer, Platform.PathComparison()) || inner.StartsWith(outer + Path.DirectorySeparatorChar, Platform.PathComparison());
    }

    private static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), Platform.PathComparison());

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? GitPath(string root, string folder)
    {
        var relative = Path.GetRelativePath(Normalize(root), Normalize(folder)).Replace('\\', '/');
        if (relative == ".") return "";
        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative) ? null : relative;
    }

    private static string RelativeTo(string project, string? folder) =>
        folder is null ? "" : GitPath(project, folder) is { Length: > 0 } relative ? relative : folder;

    private static string DescribeUpstream(BasisDevUpstream upstream)
    {
        var text = string.IsNullOrWhiteSpace(upstream.Url) ? "unknown upstream" : upstream.Url;
        if (!string.IsNullOrWhiteSpace(upstream.Path)) text += " (" + upstream.Path + ")";
        if (!string.IsNullOrWhiteSpace(upstream.Ref)) text += " #" + upstream.Ref;
        if (upstream.Commit is { Length: >= 7 } commit) text += " @ " + commit[..7];
        return text;
    }

    private static string DescribeSource(BasisDevPackage package) => package.Source switch
    {
        PackageSourceKind.ProjectRepo or PackageSourceKind.LocalFolder => "Packages/" + Path.GetFileName(package.EmbeddedFolder),
        _ => package.ManifestValue ?? "its manifest entry",
    };
}
