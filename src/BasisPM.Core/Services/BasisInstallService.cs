using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisInstallService
{
    public const string BasisRepoUrl = "https://github.com/BasisVR/Basis.git";
    public const string DefaultBranch = "developer";

    private readonly UnityProjectService _projects;
    private readonly GitService _git;

    public BasisInstallService(UnityProjectService projects, GitService git)
    {
        _projects = projects;
        _git = git;
    }

    public async Task<BasisInstall> LoadAsync(string repoRoot, string? alias = null, CancellationToken ct = default)
    {
        var detection = _projects.Detect(repoRoot);
        var unityPath = detection.IsValid && detection.ResolvedPath is not null ? detection.ResolvedPath : repoRoot;
        var hasUnity = detection.IsValid;

        var version = "unknown";
        var manifest = new PackageManifest();
        if (hasUnity)
        {
            try
            {
                var info = await _projects.LoadAsync(unityPath, ct).ConfigureAwait(false);
                version = info.UnityVersion;
                manifest = info.Manifest;
            }
            catch (Exception ex) { DiagnosticLog.Write($"Loading Unity project metadata from {unityPath}", ex); }
        }

        return new BasisInstall
        {
            RepoRoot = repoRoot,
            UnityProjectPath = unityPath,
            Name = new DirectoryInfo(repoRoot).Name,
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            UnityVersion = version,
            IsGitRepo = _git.IsGitRepo(repoRoot),
            HasUnityProject = hasUnity,
            IsBasisCheckout = hasUnity && HasBasisPackage(unityPath),
            Manifest = manifest,
        };
    }

    public UnityProjectInfo ToProjectInfo(BasisInstall install) => new()
    {
        Path = install.UnityProjectPath,
        Name = install.Name,
        UnityVersion = install.UnityVersion,
        Manifest = install.Manifest,
    };

    public bool IsBasisCheckout(string path)
    {
        var detection = _projects.Detect(path);
        return detection.IsValid
            && detection.ResolvedPath is not null
            && HasBasisPackage(detection.ResolvedPath);
    }

    private static bool HasBasisPackage(string unityProjectPath) =>
        File.Exists(Path.Combine(unityProjectPath, "Packages", "com.basis.framework", "package.json"));

    public const int ProjectSearchDepth = 8;
    public const int RepositorySearchDepth = 2;
    private static readonly EnumerationOptions SearchOptions = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
    private static readonly HashSet<string> SkippedSearchFolders = new(StringComparer.OrdinalIgnoreCase) { "node_modules", "bin", "obj" };
    private static readonly HashSet<string> SkippedDriveFolders = new(StringComparer.OrdinalIgnoreCase) { "Windows", "Program Files", "Program Files (x86)", "PerfLogs" };

    public IEnumerable<FoundProject> FindProjects(string folder, Action<int>? progress = null, CancellationToken ct = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var pending = new Queue<(string Path, int Depth, int InRepository)>();
        pending.Enqueue((root, 0, -1));
        var searched = 0;
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, depth, inRepository) = pending.Dequeue();
            progress?.Invoke(++searched);
            if (_projects.IsUnityProject(path))
            {
                if (HasBasisPackage(path))
                {
                    var repoRoot = RepoRootFor(path, root);
                    yield return new FoundProject(repoRoot, path, UnityProjectService.ReadUnityVersion(path), SuggestedName(repoRoot, root));
                }
                continue;
            }
            if (inRepository < 0 && depth > 0 && _git.IsGitRepo(path)) inRepository = 0;
            if (depth >= ProjectSearchDepth || inRepository >= RepositorySearchDepth) continue;
            foreach (var sub in SearchableSubfolders(path, ct)) pending.Enqueue((sub, depth + 1, inRepository < 0 ? -1 : inRepository + 1));
        }
    }

    private static string SuggestedName(string repoRoot, string searchRoot)
    {
        var dir = new DirectoryInfo(repoRoot);
        while (IsBasisFolder(dir.Name) && !Platform.PathsEqual(dir.FullName, searchRoot) && dir.Parent is { } parent && !Platform.PathsEqual(parent.FullName, searchRoot))
            dir = parent;
        if (IsBasisFolder(dir.Name)) return "Basis";
        return dir.Name.Contains("Basis", StringComparison.OrdinalIgnoreCase) ? dir.Name : "Basis " + dir.Name;
    }

    private static bool IsBasisFolder(string name) => name.Equals("Basis", StringComparison.OrdinalIgnoreCase);

    private string RepoRootFor(string unityProjectPath, string searchRoot)
    {
        for (var dir = new DirectoryInfo(unityProjectPath); dir is not null; dir = dir.Parent)
        {
            if (_git.IsGitRepo(dir.FullName))
                return Platform.PathsEqual(_projects.Detect(dir.FullName).ResolvedPath, unityProjectPath) ? dir.FullName : unityProjectPath;
            if (Platform.PathsEqual(dir.FullName, searchRoot)) break;
        }
        return unityProjectPath;
    }

    private static List<string> SearchableSubfolders(string path, CancellationToken ct)
    {
        var folders = new List<string>();
        var driveRoot = Platform.PathsEqual(Path.GetPathRoot(path), path);
        try
        {
            foreach (var dir in new DirectoryInfo(path).EnumerateDirectories("*", SearchOptions))
            {
                ct.ThrowIfCancellationRequested();
                if (!dir.Name.StartsWith('.') && !SkippedSearchFolders.Contains(dir.Name) && !(driveRoot && SkippedDriveFolders.Contains(dir.Name))
                    && ((dir.Attributes & FileAttributes.ReparsePoint) == 0 || dir.LinkTarget is null))
                    folders.Add(dir.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            DiagnosticLog.Write($"Searching {path} for Basis projects", ex);
        }
        return folders;
    }

    /// <summary>
    /// Permanently deletes an install's folder (the whole clone) from disk. Runs off the calling
    /// thread because a Basis checkout plus its Unity <c>Library</c> can be tens of gigabytes.
    /// </summary>
    public static Task DeleteFolderAsync(string root) => Task.Run(() => ForceDelete(root));

    // Git marks packed objects read-only on Windows, so a plain Directory.Delete would throw
    // UnauthorizedAccessException partway through — clear the attribute on every entry first.
    private static void ForceDelete(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        var full = Path.GetFullPath(root);

        // Never delete a drive root — a corrupt/misconfigured path must not wipe an entire volume.
        var pathRoot = Path.GetPathRoot(full) ?? "";
        if (string.Equals(Path.TrimEndingDirectorySeparator(full),
                          Path.TrimEndingDirectorySeparator(pathRoot),
                          StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to delete a drive root: {full}");

        var dir = new DirectoryInfo(full);
        if (!dir.Exists) return;

        dir.Attributes = FileAttributes.Directory;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            foreach (var info in pending.Pop().EnumerateFileSystemInfos())
            {
                if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    info.Attributes &= ~FileAttributes.ReadOnly;
                if (info is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                    pending.Push(sub);
            }
        }

        dir.Delete(recursive: true);
    }
}
