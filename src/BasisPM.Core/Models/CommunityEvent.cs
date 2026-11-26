using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

/// <summary>
/// A single community event shown in the Home page's Events section. The feed is a JSON array hosted on the
/// static website (basisvr.org/events.json) and hand-edited to post; the app fetches it, falling back to an
/// embedded copy when offline.
/// </summary>
public sealed class CommunityEvent
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    // ISO 8601 with an offset or Z (2026-12-14T19:00:00Z). A value with no offset is read as UTC.
    [JsonPropertyName("start")] public string? Start { get; set; }
    [JsonPropertyName("end")] public string? End { get; set; }

    // "weekly" repeats the event every seven days from its start; anything else means it happens once.
    [JsonPropertyName("repeat")] public string? Repeat { get; set; }

    // Who runs it, and where it happens (a world, a server, a Discord stage).
    [JsonPropertyName("host")] public string? Host { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }

    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("linkText")] public string? LinkText { get; set; }
}
