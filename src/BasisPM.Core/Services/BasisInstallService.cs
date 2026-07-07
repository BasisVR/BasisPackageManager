using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisInstallService
{
    public const string BasisRepoUrl = "https://github.com/BasisVR/Basis";
    public const string DefaultFolderName = "Basis";
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
            catch { }
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

    public bool IsUnityProject(string path) => _projects.IsUnityProject(path);

    public async Task<BasisInstall> CreateNewProjectAsync(string rootPath, string unityVersion, string? alias = null, CancellationToken ct = default)
    {
        await _projects.CreateNewProjectAsync(rootPath, unityVersion, ct).ConfigureAwait(false);
        return await LoadAsync(rootPath, alias, ct).ConfigureAwait(false);
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
        foreach (var info in dir.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                info.Attributes &= ~FileAttributes.ReadOnly;

        dir.Delete(recursive: true);
    }
}
