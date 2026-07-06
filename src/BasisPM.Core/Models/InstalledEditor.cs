namespace BasisPM.Core.Models;

public sealed class InstalledEditor
{
    public required string Version { get; init; }
    public required string Path { get; init; }
    public List<string> Modules { get; init; } = new();

    // True for editors the user registered by hand (no Unity Hub involved). These are launched
    // directly and are "removed" (forgotten) rather than uninstalled — we never touch their files.
    public bool IsManual { get; init; }
}
