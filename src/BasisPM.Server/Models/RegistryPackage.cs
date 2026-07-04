using System.Text.Json.Serialization;

namespace BasisPM.Server.Models;

public sealed class RegistryPackage
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("authorUrl")] public string? AuthorUrl { get; set; }
    [JsonPropertyName("category")] public string Category { get; set; } = "Misc";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();

    // curated | community
    [JsonPropertyName("source")] public string Source { get; set; } = "community";

    // UPM git dependency URL — works for both GitHub and GitLab.
    [JsonPropertyName("gitUrl")] public string? GitUrl { get; set; }
    [JsonPropertyName("repoUrl")] public string? RepoUrl { get; set; }

    [JsonPropertyName("unity")] public string? Unity { get; set; }
    // The Basis version this package was built/tested against (free-form: branch, tag or version — e.g. "main" or "1.2.0").
    [JsonPropertyName("basisVersion")] public string? BasisVersion { get; set; }
    [JsonPropertyName("version")] public string Version { get; set; } = "0.0.0";
    [JsonPropertyName("stars")] public int Stars { get; set; }
    [JsonPropertyName("forks")] public int Forks { get; set; }
    [JsonPropertyName("icon")] public string Icon { get; set; } = "📦";
    // Optional promo image. Self-hosted only: a path under the site's icons/ folder
    // (e.g. "icons/com.basis.sdk.png"), never an arbitrary remote URL. Emoji Icon is the fallback.
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }

    // Optional per-package community + funding links, shown on the package's registry page.
    [JsonPropertyName("discord")] public string? Discord { get; set; }
    [JsonPropertyName("donate")] public string? Donate { get; set; }
}

public sealed class RegistrySubmission
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("authorUrl")] public string? AuthorUrl { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("gitUrl")] public string? GitUrl { get; set; }
    [JsonPropertyName("repoUrl")] public string? RepoUrl { get; set; }
    [JsonPropertyName("unity")] public string? Unity { get; set; }
    [JsonPropertyName("basisVersion")] public string? BasisVersion { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
    [JsonPropertyName("discord")] public string? Discord { get; set; }
    [JsonPropertyName("donate")] public string? Donate { get; set; }
}
