namespace BasisPM.Core.Models;

/// <summary>
/// Outcome of driving an install's Unity editor in batch mode to run com.basis.setup.
/// <see cref="EditorFound"/> is false when no editor matching the project's version is available;
/// otherwise <see cref="ExitCode"/> is Unity's process exit code (0 = success) and
/// <see cref="LogPath"/> points at the captured editor log.
/// </summary>
public sealed record UnitySetupRunResult(bool EditorFound, int ExitCode, string? LogPath)
{
    public static readonly UnitySetupRunResult NoEditor = new(false, -1, null);

    public bool Success => EditorFound && ExitCode == 0;
}
