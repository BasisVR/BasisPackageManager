using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class Catalog
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("packages")]
    public Dictionary<string, CatalogPackage> Packages { get; set; } = new();
}

public sealed class CatalogPackage
{
    [JsonPropertyName("versions")]
    public Dictionary<string, CatalogPackageVersion> Versions { get; set; } = new();
}

public sealed class CatalogPackageVersion
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("unity")] public string? Unity { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("author")] public CatalogAuthor? Author { get; set; }
    // Optional self-hosted promo image URL (served from the registry's icons/ folder).
    [JsonPropertyName("image")] public string? Image { get; set; }
}

public sealed class CatalogAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
}
