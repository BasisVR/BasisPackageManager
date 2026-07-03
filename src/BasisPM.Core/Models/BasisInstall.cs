namespace BasisPM.Core.Models;

public sealed class BasisInstall
{
    public required string RepoRoot { get; init; }
    public required string UnityProjectPath { get; init; }
    public required string Name { get; init; }
    public string UnityVersion { get; set; } = "unknown";
    public bool IsGitRepo { get; init; }
    public bool HasUnityProject { get; init; }
    public PackageManifest Manifest { get; set; } = new();
}
