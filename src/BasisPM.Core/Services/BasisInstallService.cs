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

    public async Task<BasisInstall> LoadAsync(string repoRoot, CancellationToken ct = default)
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
}
