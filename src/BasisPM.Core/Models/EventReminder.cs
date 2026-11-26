using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

/// <summary>A reminder the user asked for on one community event, saved in the user settings under the event's id.</summary>
public sealed class EventReminder
{
    // True reminds before every occurrence of a repeating event; false reminds for one occurrence only.
    [JsonPropertyName("always")] public bool Always { get; set; }

    // The start of the one occurrence a just-once reminder is for.
    [JsonPropertyName("occurrence")] public string? Occurrence { get; set; }

    // The start of the last occurrence already notified, so it is not shown twice.
    [JsonPropertyName("notified")] public string? Notified { get; set; }
}
