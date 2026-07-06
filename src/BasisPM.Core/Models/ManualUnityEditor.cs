using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

/// <summary>
/// A Unity editor the user pointed us at by hand — the escape hatch for people without Unity Hub.
/// Persisted in <see cref="UserSettings.ManualEditors"/>. Plain, non-required properties so a
/// malformed or partial entry can never throw during settings load (which would reset everything).
/// </summary>
public sealed class ManualUnityEditor
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    public InstalledEditor ToInstalledEditor() => new() { Version = Version, Path = Path, IsManual = true };
}
