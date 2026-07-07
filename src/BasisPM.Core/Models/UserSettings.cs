using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class UserSettings
{
    [JsonPropertyName("installs")]
    public List<string> Installs { get; set; } = new();

    // Human-friendly alias per install, keyed by repo-root path.
    [JsonPropertyName("installAliases")]
    public Dictionary<string, string> InstallAliases { get; set; } = new();

    [JsonPropertyName("clonePath")]
    public string? ClonePath { get; set; }

    [JsonPropertyName("catalogUrl")]
    public string CatalogUrl { get; set; } = "";

    // Additional, user-added catalog URLs. These are UNOFFICIAL (not vetted by BasisVR): their
    // packages are merged into the list and badged as such, and can never override an official
    // package id (the Basis catalog above always wins a conflict).
    [JsonPropertyName("extraCatalogUrls")]
    public List<string> ExtraCatalogUrls { get; set; } = new();

    [JsonPropertyName("unityHubPath")]
    public string? UnityHubPath { get; set; }

    // Unity editors the user added by hand (pointed at a folder) — the path lets people without
    // Unity Hub still open projects. Merged with the Hub-detected editors in the Unity tab.
    [JsonPropertyName("manualEditors")]
    public List<ManualUnityEditor> ManualEditors { get; set; } = new();

    // The Local Changes tab is hidden until the user opts in.
    [JsonPropertyName("showLocalChanges")]
    public bool ShowLocalChanges { get; set; }

    // Whether we've already offered a desktop shortcut on first run (so we only ask once).
    [JsonPropertyName("askedDesktopShortcut")]
    public bool AskedDesktopShortcut { get; set; }

    // Opt into the prerelease update channel — frequent, experimental builds that may be broken.
    [JsonPropertyName("prereleaseUpdates")]
    public bool PrereleaseUpdates { get; set; }

    // Ids of announcements the user has already seen (drives the unread badge on the nav).
    [JsonPropertyName("seenAnnouncementIds")]
    public List<string> SeenAnnouncementIds { get; set; } = new();

    // UI language code (e.g. "en", "ja", "zh-Hans"). Null/blank = English.
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    // Packages tab: show the "Available" list as a grid of cards instead of rows.
    [JsonPropertyName("packagesGridView")]
    public bool PackagesGridView { get; set; }
}
