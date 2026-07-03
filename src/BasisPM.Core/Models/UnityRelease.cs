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

    public string Display => Recommended
        ? $"{Version}  ·  {Stream}  ·  recommended"
        : $"{Version}  ·  {Stream}";
}
