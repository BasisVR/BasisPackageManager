namespace BasisPM.Core.Models;

public sealed class InstalledEditor
{
    public required string Version { get; init; }
    public required string Path { get; init; }
    public List<string> Modules { get; init; } = new();
}
