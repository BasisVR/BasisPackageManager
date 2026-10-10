using System.Text;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisPartsService
{
    public static readonly BasisPart Server = new("server", "Basis Server", ServerPackageService.ServerFolderName, false);
    public static readonly BasisPart Images = new("images", "Images", "Images", true);
    public static readonly IReadOnlyList<BasisPart> All = new[] { Server, Images };

    private const string EverythingPattern = "/*";

    private readonly GitService _git;

    public BasisPartsService(GitService git) => _git = git;

    public static BasisPart? Find(string? name)
    {
        var key = (name ?? "").Trim();
        return All.FirstOrDefault(p => key.Equals(p.Id, StringComparison.OrdinalIgnoreCase) || key.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<BasisPart> FromIds(IEnumerable<string>? ids) => (ids ?? Array.Empty<string>()).Select(Find).OfType<BasisPart>().Distinct().ToList();

    public static string PathOf(BasisPart part, string unityFolder) => part.InUnityProject && unityFolder.Length > 0 ? unityFolder + "/" + part.Folder : part.Folder;

    public static IReadOnlyList<string> ClonePatterns(IEnumerable<BasisPart> leaveOut) => Patterns(leaveOut.Select(p => PathOf(p, BasisUpdateService.BasisUnityFolder)));

    public static IReadOnlyList<string> Patterns(IEnumerable<string> leftOut)
    {
        var excluded = leftOut.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path => "!/" + Escape(path) + "/").ToList();
        return excluded.Count == 0 ? Array.Empty<string>() : excluded.Prepend(EverythingPattern).ToList();
    }

    public async Task<BasisPartsReport> ScanAsync(string repoRoot, string unityProjectPath, bool inspect = true, CancellationToken ct = default)
    {
        var top = await _git.GetTopLevelAsync(repoRoot, ct).ConfigureAwait(false);
        if (top is null) return new BasisPartsReport(BasisPartsMode.NotGitRepo, null, Array.Empty<BasisPartState>());
        var unityFolder = UnityFolder(top, unityProjectPath);
        var paths = All.Where(p => !p.InUnityProject || unityFolder is not null).Select(p => (Part: p, Path: PathOf(p, unityFolder ?? ""))).ToList();
        var sparse = await _git.GetSparseCheckoutAsync(top, ct).ConfigureAwait(false);
        var leftOut = sparse.Enabled ? LeftOutPaths(sparse, paths.Select(p => p.Path)) : new HashSet<string>(StringComparer.Ordinal);
        var mode = !sparse.Enabled ? BasisPartsMode.Complete : leftOut is null ? BasisPartsMode.Custom : BasisPartsMode.Managed;
        var parts = new List<BasisPartState>();
        foreach (var (part, path) in paths)
        {
            var full = FullPath(top, path);
            var recorded = leftOut?.Contains(path) == true;
            if (!recorded && !await _git.IsTrackedInHeadAsync(top, path, ct).ConfigureAwait(false)) continue;
            var included = leftOut is null ? Directory.Exists(full) : !recorded;
            parts.Add(included && inspect && Directory.Exists(full)
                ? await InspectAsync(top, part, path, ct).ConfigureAwait(false)
                : new BasisPartState(part, path, included, Array.Empty<string>(), 0, Array.Empty<string>()));
        }
        return new BasisPartsReport(mode, top, parts);
    }

    public async Task<IReadOnlyList<BasisPart>> LeftOutAsync(string repoRoot, string unityProjectPath, CancellationToken ct = default)
    {
        if (!_git.IsGitRepo(repoRoot) || !await _git.GetConfigFlagAsync(repoRoot, "core.sparseCheckout", ct).ConfigureAwait(false)) return Array.Empty<BasisPart>();
        return (await ScanAsync(repoRoot, unityProjectPath, false, ct).ConfigureAwait(false)).LeftOut;
    }

    public async Task<BasisPartsResult> ApplyAsync(string repoRoot, string unityProjectPath, IReadOnlyCollection<BasisPart> leaveOut, bool deleteIgnored, CancellationToken ct = default)
    {
        var report = await ScanAsync(repoRoot, unityProjectPath, true, ct).ConfigureAwait(false);
        if (report.RepositoryRoot is not { } top) return BasisPartsResult.Fail(BasisPartsFailure.NotGitRepo, repoRoot);
        if (!report.CanChange) return BasisPartsResult.Fail(BasisPartsFailure.CustomSparseCheckout, top);
        var removing = report.Parts.Where(p => p.Included && leaveOut.Contains(p.Part)).ToList();
        var adding = report.Parts.Where(p => !p.Included && !leaveOut.Contains(p.Part)).ToList();
        if (removing.Count == 0 && adding.Count == 0) return BasisPartsResult.Unchanged;
        if (await OperationInProgressAsync(top, ct).ConfigureAwait(false) is { } operation) return BasisPartsResult.Fail(BasisPartsFailure.OperationInProgress, operation);
        var unsaved = removing.SelectMany(p => p.Unsaved).ToList();
        if (unsaved.Count > 0) return BasisPartsResult.Fail(BasisPartsFailure.UnsavedWork, string.Join('\n', unsaved));

        var leftOut = report.Parts.Where(p => !p.Included && !adding.Contains(p)).Concat(removing).Select(p => p.Path);
        var patterns = Patterns(leftOut);
        var changed = patterns.Count == 0
            ? await _git.DisableSparseCheckoutAsync(top, ct).ConfigureAwait(false)
            : await _git.SetSparseCheckoutAsync(top, patterns, ct).ConfigureAwait(false);
        if (!changed.Ok) return BasisPartsResult.Fail(BasisPartsFailure.GitFailed, changed.Output);

        int deleted = 0, left = 0;
        var kept = new List<string>();
        foreach (var part in removing)
        {
            var leftovers = await ClearLeftoversAsync(top, part.Path, deleteIgnored, ct).ConfigureAwait(false);
            deleted += leftovers.Deleted;
            left += leftovers.Left;
            kept.AddRange(leftovers.Kept);
        }
        return new BasisPartsResult(BasisPartsFailure.None, "", adding.Select(p => p.Part).ToList(), removing.Select(p => p.Part).ToList(), deleted, left, kept);
    }

    private async Task<BasisPartState> InspectAsync(string top, BasisPart part, string path, CancellationToken ct)
    {
        var entries = await _git.GetWorkingEntriesAsync(top, path, true, ct).ConfigureAwait(false);
        if (entries is null) return new BasisPartState(part, path, true, new[] { path }, 0, Array.Empty<string>());
        var unsaved = new List<string>();
        var repositories = new List<string>();
        var ignored = 0;
        foreach (var entry in entries)
        {
            if (entry.Code == "!!")
            {
                if (entry.Path.EndsWith('/')) repositories.Add(entry.Path.TrimEnd('/'));
                else ignored++;
            }
            else if (entry.Code != " D") unsaved.Add(entry.Path);
        }
        return new BasisPartState(part, path, true, unsaved, ignored, repositories);
    }

    private async Task<string?> OperationInProgressAsync(string top, CancellationToken ct)
    {
        if (await _git.GetOperationInProgressAsync(top, ct).ConfigureAwait(false) is { } operation) return operation;
        if (await _git.GetGitDirAsync(top, ct).ConfigureAwait(false) is { } gitDir && File.Exists(Path.Combine(gitDir, BasisUpdateService.StateFileName))) return "Basis update";
        return (await _git.GetConflictsAsync(top, ct).ConfigureAwait(false)).Count > 0 ? "merge" : null;
    }

    private async Task<(int Deleted, int Left, List<string> Kept)> ClearLeftoversAsync(string top, string path, bool deleteIgnored, CancellationToken ct)
    {
        var folder = FullPath(top, path);
        if (!Directory.Exists(folder)) return (0, 0, new List<string>());
        var entries = await _git.GetWorkingEntriesAsync(top, path, true, ct).ConfigureAwait(false);
        if (entries is null) return (0, 0, new List<string> { path });
        var kept = entries.Where(e => e.Code != "!!" || e.Path.EndsWith('/')).Select(e => e.Path.TrimEnd('/')).ToList();
        var ignored = entries.Where(e => e.Code == "!!" && !e.Path.EndsWith('/')).Select(e => e.Path).ToList();
        if (!deleteIgnored) return (0, ignored.Count, kept);
        var deleted = 0;
        foreach (var file in ignored)
        {
            if (IsInside(FullPath(top, file), folder) && TryDelete(FullPath(top, file))) deleted++;
            else kept.Add(file);
        }
        PruneEmptyFolders(folder);
        return (deleted, 0, kept);
    }

    private static HashSet<string>? LeftOutPaths(GitSparseCheckout sparse, IEnumerable<string> known)
    {
        if (sparse.Cone) return null;
        var lines = sparse.Patterns.Select(p => p.Trim()).Where(p => p.Length > 0 && !p.StartsWith('#')).ToList();
        if (lines.Count == 0 || lines[0] != EverythingPattern) return null;
        var names = known.ToHashSet(StringComparer.Ordinal);
        var leftOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var path = line.Length > 3 && line.StartsWith("!/", StringComparison.Ordinal) && line.EndsWith('/') ? Unescape(line[2..^1]) : null;
            if (path is null || !names.Contains(path)) return null;
            leftOut.Add(path);
        }
        return leftOut;
    }

    private static string? UnityFolder(string top, string unityProjectPath)
    {
        if (string.IsNullOrWhiteSpace(unityProjectPath)) return null;
        var relative = Path.GetRelativePath(top, Path.GetFullPath(unityProjectPath)).Replace('\\', '/');
        if (relative == ".") return "";
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    private static string Escape(string path)
    {
        var text = new StringBuilder(path.Length);
        foreach (var c in path)
        {
            if (c is '\\' or '*' or '?' or '[' or ']') text.Append('\\');
            text.Append(c);
        }
        return text.ToString();
    }

    private static string Unescape(string pattern)
    {
        var text = new StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 1 < pattern.Length) i++;
            text.Append(pattern[i]);
        }
        return text.ToString();
    }

    private static string FullPath(string top, string path) => Path.Combine(top, path.Replace('/', Path.DirectorySeparatorChar));

    private static bool IsInside(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            else if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) Directory.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Deleting {path}", ex);
            return false;
        }
    }

    private static void PruneEmptyFolders(string folder)
    {
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
            {
                var linked = (File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0;
                if (!linked && !Directory.Exists(Path.Combine(sub, ".git")) && !File.Exists(Path.Combine(sub, ".git"))) PruneEmptyFolders(sub);
            }
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Removing empty folders in {folder}", ex);
        }
    }
}
