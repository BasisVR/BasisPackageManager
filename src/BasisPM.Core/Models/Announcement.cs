using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

/// <summary>
/// A single project announcement shown in the app's Announcements section. The feed is a JSON
/// array hosted on the static website (basisvr.org/announcements.json) and hand-edited to post;
/// the app fetches it, falling back to an embedded copy when offline.
/// </summary>
public sealed class Announcement
{
    // Stable unique id — used to track which announcements a user has already seen.
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("body")] public string Body { get; set; } = "";

    // ISO date (yyyy-MM-dd). Drives display and newest-first ordering.
    [JsonPropertyName("date")] public string? Date { get; set; }

    // info (default) | update | alert — drives the coloured pill.
    [JsonPropertyName("level")] public string? Level { get; set; }

    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("linkText")] public string? LinkText { get; set; }

    // Keeps the item at the top of the list regardless of date.
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
}
