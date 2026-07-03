using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class PackageManifest
{
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    [JsonPropertyName("scopedRegistries")]
    public List<ScopedRegistry>? ScopedRegistries { get; set; }

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class ScopedRegistry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("scopes")] public List<string> Scopes { get; set; } = new();
}
