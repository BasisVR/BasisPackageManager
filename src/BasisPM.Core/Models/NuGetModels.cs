using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class NuGetSearchResponse
{
    [JsonPropertyName("totalHits")] public int TotalHits { get; set; }
    [JsonPropertyName("data")] public List<NuGetPackage> Data { get; set; } = new();
}

public sealed class NuGetPackage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("authors")] public List<string> Authors { get; set; } = new();
    [JsonPropertyName("totalDownloads")] public long TotalDownloads { get; set; }
    [JsonPropertyName("verified")] public bool Verified { get; set; }
    [JsonPropertyName("projectUrl")] public string? ProjectUrl { get; set; }

    public string AuthorLine => Authors.Count > 0 ? string.Join(", ", Authors) : "unknown author";

    public string DownloadsLabel => TotalDownloads switch
    {
        >= 1_000_000_000 => $"{TotalDownloads / 1_000_000_000d:0.#}B downloads",
        >= 1_000_000 => $"{TotalDownloads / 1_000_000d:0.#}M downloads",
        >= 1_000 => $"{TotalDownloads / 1_000d:0.#}K downloads",
        _ => $"{TotalDownloads} downloads",
    };
}

public sealed record NuGetInstalled(string Id, string Version);
