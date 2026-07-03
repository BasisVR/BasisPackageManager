using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed record GitHubLocator(string Owner, string Repo, string? Branch, string? Path);

public sealed class UpmPackageJson
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("unity")] public string? Unity { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
}
