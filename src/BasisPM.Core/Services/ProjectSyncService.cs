using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record ProjectSyncResult(bool Ok, string Message)
{
    public bool Pulled { get; init; }
    public int Commits { get; init; }
    public IReadOnlyList<string> Conflicts { get; init; } = Array.Empty<string>();
    public bool HasConflicts => Conflicts.Count > 0;
    public IReadOnlyList<string> Blocking { get; init; } = Array.Empty<string>();
    public bool IsBlocked => Blocking.Count > 0;
    public string? Upstream { get; init; }
    public string? BackupFolder { get; init; }
    public BasisDevResult? Packages { get; init; }
}

public sealed class ProjectSyncService
{
    private readonly GitService _git;
    private readonly BasisDevService _basisDev;
    private readonly UnityProjectService _projects;

    public ProjectSyncService(GitService git, BasisDevService basisDev, UnityProjectService projects)
    {
        _git = git;
        _basisDev = basisDev;
        _projects = projects;
    }

    public async Task<ProjectSyncResult> SyncAsync(BasisInstall install, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return new ProjectSyncResult(false, "Git was not found on PATH.");
        var pulled = false;
        var commits = 0;
        string? pullNote = null;
        if (install.IsGitRepo)
        {
            if (await _git.GetOperationInProgressAsync(install.RepoRoot, ct).ConfigureAwait(false) is { } operation)
                return new ProjectSyncResult(false, $"The project is in the middle of a {operation}. Finish or abort it first.");
            var conflicts = (await _git.GetConflictsAsync(install.RepoRoot, ct).ConfigureAwait(false)).Select(c => c.Path).ToList();
            if (conflicts.Count > 0) return new ProjectSyncResult(false, DescribeConflicts(conflicts)) { Conflicts = conflicts };
            var upstream = await _git.GetUpstreamNameAsync(install.RepoRoot, ct).ConfigureAwait(false);
            if (upstream is null) pullNote = "The current branch doesn't track a remote branch, so nothing was pulled.";
            else
            {
                onProgress?.Invoke($"Pulling from {upstream}…");
                var before = await _git.ResolveCommitAsync(install.RepoRoot, "HEAD", ct).ConfigureAwait(false);
                var pull = await _git.PullAsync(install.RepoRoot, onProgress, ct).ConfigureAwait(false);
                if (!pull.Ok)
                {
                    var left = (await _git.GetConflictsAsync(install.RepoRoot, ct).ConfigureAwait(false)).Select(c => c.Path).ToList();
                    if (left.Count > 0) return new ProjectSyncResult(false, DescribeConflicts(left)) { Conflicts = left };
                    var blocking = IsOverwriteRefusal(pull.Output) ? await BlockingFilesAsync(install.RepoRoot, upstream, ct).ConfigureAwait(false) : Array.Empty<string>();
                    return new ProjectSyncResult(false, DescribePullFailure(upstream, pull.Output)) { Blocking = blocking, Upstream = upstream };
                }
                var after = await _git.ResolveCommitAsync(install.RepoRoot, "HEAD", ct).ConfigureAwait(false);
                if (before is not null && after is not null && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                {
                    pulled = true;
                    commits = await _git.CountCommitsAsync(install.RepoRoot, $"{before}..{after}", ct).ConfigureAwait(false);
                }
            }
        }

        try { install.Manifest = (await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false)).Manifest; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException) { DiagnosticLog.Write($"Reloading {install.UnityProjectPath} after a pull", ex); }

        var packages = await _basisDev.RestoreAllAsync(install, switchListed: true, onProgress, ct).ConfigureAwait(false);
        var pullText = pulled ? $"Pulled {commits} commit{(commits == 1 ? "" : "s")}." : pullNote ?? "Already up to date with the remote.";
        return new ProjectSyncResult(packages.Ok, pullText + " " + packages.Message) { Pulled = pulled, Commits = commits, Packages = packages };
    }

    public static bool IsOverwriteRefusal(string output) => output.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> BlockingFilesAsync(string repoRoot, string upstream, CancellationToken ct = default)
    {
        var head = await _git.ResolveCommitAsync(repoRoot, "HEAD", ct).ConfigureAwait(false);
        var theirs = await _git.ResolveCommitAsync(repoRoot, upstream, ct).ConfigureAwait(false);
        if (head is null || theirs is null) return Array.Empty<string>();
        var incoming = (await _git.DiffTreesAsync(repoRoot, head, theirs, ct).ConfigureAwait(false) ?? Array.Empty<GitTreeChange>()).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        return (await _git.GetChangesAsync(repoRoot, ct).ConfigureAwait(false)).Select(c => c.Path).Where(incoming.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Backs up the files that blocked a pull, clears them so the pull can go through, pulls, then either
    /// puts the copies back (keepMine) or leaves the incoming versions. Copies stay in the backup folder either way.</summary>
    public async Task<ProjectSyncResult> SyncSettingAsideAsync(BasisInstall install, IReadOnlyList<string> paths, bool keepMine, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (paths.Count == 0) return await SyncAsync(install, onProgress, ct).ConfigureAwait(false);
        if (paths.Any(p => !GitUrlPolicy.IsSafeSubPath(p))) return new ProjectSyncResult(false, "One of the files has a path BPM won't touch.");
        var root = install.RepoRoot;
        var gitDir = await _git.GetGitDirAsync(root, ct).ConfigureAwait(false) ?? Path.Combine(root, ".git");
        var backup = Path.Combine(gitDir, "basispm-sync-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        var present = new List<string>();
        var gone = new List<string>();
        onProgress?.Invoke($"Backing up {paths.Count} file(s)…");
        try
        {
            foreach (var path in paths)
            {
                var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(file)) { gone.Add(path); continue; }
                var copy = Path.Combine(backup, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(file, copy, overwrite: true);
                present.Add(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ProjectSyncResult(false, $"Couldn't back up the files, so nothing was changed: {ex.Message}");
        }

        var trackedPaths = paths.Where(tracked.Contains).ToList();
        if (trackedPaths.Count > 0)
        {
            await _git.UnstagePathsAsync(root, trackedPaths, ct).ConfigureAwait(false);
            var cleared = await _git.RestorePathsFromHeadAsync(root, trackedPaths, ct).ConfigureAwait(false);
            if (!cleared.Ok)
            {
                PutBack(root, backup, present);
                return new ProjectSyncResult(false, $"Couldn't clear the edited files, so nothing was pulled. Your versions are back in place: {cleared.Output}");
            }
        }
        foreach (var path in present.Where(p => !tracked.Contains(p)))
            File.Delete(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

        var before = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var result = await SyncAsync(install, onProgress, ct).ConfigureAwait(false);
        var after = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var moved = before is not null && after is not null && before != after;
        if (keepMine || !moved)
        {
            PutBack(root, backup, present);
            foreach (var path in gone)
            {
                var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(file)) File.Delete(file);
            }
        }
        var note = !moved ? $" Your {paths.Count} file(s) are back as they were."
            : keepMine ? $" Your version of {paths.Count} file(s) is back in place; git shows how it differs from the new commit. Copies are in {backup}."
            : $" {paths.Count} file(s) now match the remote. Your versions are saved in {backup}.";
        return new ProjectSyncResult(result.Ok, result.Message + note) { Pulled = result.Pulled, Commits = result.Commits, Packages = result.Packages, Conflicts = result.Conflicts, Blocking = result.Blocking, Upstream = result.Upstream, BackupFolder = backup };
    }

    private static void PutBack(string root, string backup, IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var copy = Path.Combine(backup, path.Replace('/', Path.DirectorySeparatorChar));
            var file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(copy, file, overwrite: true);
        }
    }

    public static string DescribeConflicts(IReadOnlyList<string> paths)
    {
        var names = string.Join(", ", paths.Take(3).Select(Path.GetFileName));
        var more = paths.Count > 3 ? $" and {paths.Count - 3} more" : "";
        return paths.Count == 1
            ? $"{names} still has a merge conflict, so nothing was pulled. Fix it, then sync again."
            : $"{paths.Count} files still have merge conflicts ({names}{more}), so nothing was pulled. Fix them, then sync again.";
    }

    public static string DescribePullFailure(string upstream, string output)
    {
        if (output.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase) || output.Contains("diverg", StringComparison.OrdinalIgnoreCase))
            return $"Your branch and {upstream} have both changed. Merge or rebase them in your git tool first, then sync again.";
        if (output.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
            return $"Your uncommitted edits touch files that changed on {upstream}. Commit or set them aside first, then sync again.";
        return $"Couldn't pull from {upstream}: {output}";
    }
}
