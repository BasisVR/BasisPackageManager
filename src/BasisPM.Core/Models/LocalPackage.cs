namespace BasisPM.Core.Models;

/// <summary>An embedded (on-disk) UPM package found under a Unity project's <c>Packages/</c> folder.</summary>
public sealed record LocalPackage(
    string Id,
    string DisplayName,
    string Version,
    string Description,
    string FolderName,
    string FolderPath,
    string PackageJsonPath,
    bool IsGitRepo);
