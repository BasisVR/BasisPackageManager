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
    // SPDX license string or expression (e.g. "MIT"), surfaced in the desktop client.
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("author")] public CatalogAuthor? Author { get; set; }
    // Optional self-hosted promo image URL (served from the registry's icons/ folder).
    [JsonPropertyName("image")] public string? Image { get; set; }
    // Optional emoji shown as the package's icon tile in the desktop client (falls back to a letter).
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    // Optional author-provided link (homepage / showcase / docs), surfaced in the desktop client.
    [JsonPropertyName("link")] public string? Link { get; set; }

    // Registry metadata (from the server's RegistryPackage) that drives the desktop package filters:
    // discovery category, provenance (official / community / built-in), search tags, and GitHub
    // popularity / recency. Absent from older catalogs and unofficial extras, so all are nullable/zero
    // and every filter treats "missing" gracefully.
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("stars")] public int Stars { get; set; }
    [JsonPropertyName("forks")] public int Forks { get; set; }
    [JsonPropertyName("updated")] public string? Updated { get; set; }
}

public sealed class CatalogAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("url")] public string? Url { get; set; }
}
