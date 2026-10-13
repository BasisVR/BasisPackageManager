using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class UnityRelease
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("shortRevision")] public string ShortRevision { get; set; } = "";
    [JsonPropertyName("stream")] public string Stream { get; set; } = "";
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
    [JsonPropertyName("releaseDate")] public DateTime? ReleaseDate { get; set; }
    [JsonPropertyName("unityHubDeepLink")] public string? UnityHubDeepLink { get; set; }
    [JsonPropertyName("downloads")] public List<UnityReleaseDownload> Downloads { get; set; } = new();

    public string Display => Recommended
        ? $"{Version}  ·  {Stream}  ·  recommended"
        : $"{Version}  ·  {Stream}";
}

public sealed class UnityReleaseDownload
{
    [JsonPropertyName("platform")] public string Platform { get; set; } = "";
    [JsonPropertyName("architecture")] public string Architecture { get; set; } = "";
    [JsonPropertyName("modules")] public List<UnityReleaseModule> Modules { get; set; } = new();
}

public sealed class UnityReleaseModule
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
