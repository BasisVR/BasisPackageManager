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
