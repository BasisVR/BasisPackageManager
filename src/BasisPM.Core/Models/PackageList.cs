using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

/// <summary>
/// A shareable package list: a Basis version plus a set of packages. Built in the app, curated into
/// the registry (seed/packagelists.json), browsed/searched on the website, and installed back via the
/// <c>basispm://packagelist?id=…</c> deep link. Used by both the app and the server.
/// </summary>
public sealed class PackageList
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("authorUrl")] public string? AuthorUrl { get; set; }

    // The Basis core this package list was built against (informational; install-back adds packages only).
    [JsonPropertyName("basisBranch")] public string? BasisBranch { get; set; }
    [JsonPropertyName("basisCommit")] public string? BasisCommit { get; set; }
    [JsonPropertyName("unity")] public string? Unity { get; set; }

    [JsonPropertyName("icon")] public string? Icon { get; set; }      // emoji fallback
    [JsonPropertyName("image")] public string? Image { get; set; }    // optional raw image URL (page CSP-bounded)
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("updated")] public string? Updated { get; set; }

    [JsonPropertyName("packages")] public List<PackageListEntry> Packages { get; set; } = new();
}

public sealed class PackageListEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("gitUrl")] public string? GitUrl { get; set; }   // UPM git URL (community packages)
    [JsonPropertyName("version")] public string? Version { get; set; } // version range (registry/Unity packages)
}
