namespace BasisPM.Core.Models;

public sealed class UnityProjectInfo
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string UnityVersion { get; init; }
    public PackageManifest Manifest { get; set; } = new();
}
