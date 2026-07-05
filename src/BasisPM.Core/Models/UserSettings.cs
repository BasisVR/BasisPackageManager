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

    [JsonPropertyName("unityHubPath")]
    public string? UnityHubPath { get; set; }

    // The Local Changes tab is hidden until the user opts in.
    [JsonPropertyName("showLocalChanges")]
    public bool ShowLocalChanges { get; set; }

    // Developer mode reveals the Develop tab (mount / edit / submit-PR). Set by the first-run role wizard.
    [JsonPropertyName("developerMode")]
    public bool DeveloperMode { get; set; }

    // Whether the first-run role wizard has run (so we only ask once).
    [JsonPropertyName("completedOnboarding")]
    public bool CompletedOnboarding { get; set; }

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
