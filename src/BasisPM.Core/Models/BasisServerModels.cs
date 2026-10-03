namespace BasisPM.Core.Models;

public sealed record BasisServerPaths(
    string ProjectFile,
    string RuntimeDirectory,
    string ExecutablePath,
    string ConfigFile,
    string InitialResourcesDirectory,
    string DefaultLibraryDirectory);

public sealed record ServerConfigField(string Name, string Value, string Description);

public sealed record ServerContentFile(string Name, string FullPath, bool IsDefaultLibrary);

