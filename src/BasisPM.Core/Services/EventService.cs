using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>
/// Fetches the community event feed (events.json) hosted on the static website.
/// Prefers the live feed; falls back to an embedded copy when the network is unavailable.
/// </summary>
public sealed class EventService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>How long an event with no end time stays listed after it starts.</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromHours(3);

    private readonly HttpClient _http;

    public EventService(HttpClient? http = null)
    {
        _http = http ?? HttpRetryHandler.CreateClient(TimeSpan.FromSeconds(20));
    }

    /// <summary>Where events are posted: a hand-edited JSON file at the website root.</summary>
    public const string DefaultEventsUrl = "https://basisvr.org/events.json";

    /// <summary>Events that have not finished by <paramref name="now"/>, soonest first.</summary>
    public async Task<List<CommunityEvent>> LoadAsync(string? url = null, DateTimeOffset? now = null, CancellationToken ct = default)
    {
        var effective = string.IsNullOrWhiteSpace(url) ? DefaultEventsUrl : url;
        var at = now ?? DateTimeOffset.UtcNow;
        try
        {
            var list = await _http.GetFromJsonAsync<List<CommunityEvent>>(effective, JsonOpts, ct).ConfigureAwait(false);
            if (list is not null)
            {
                return Upcoming(list, at);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Loading events from {effective}", ex);
        }
        return Upcoming(LoadEmbedded(), at);
    }

    public static List<CommunityEvent> LoadEmbedded()
    {
        var asm = typeof(EventService).Assembly;
        using var stream = asm.GetManifestResourceStream("BasisPM.Core.events.json");
        if (stream is null)
        {
            return new List<CommunityEvent>();
        }
        return JsonSerializer.Deserialize<List<CommunityEvent>>(stream, JsonOpts) ?? new List<CommunityEvent>();
    }

    /// <summary>
    /// Moves repeating events on to their next occurrence, drops events with no readable start and events already over,
    /// then orders by start, then id.
    /// </summary>
    public static List<CommunityEvent> Upcoming(IEnumerable<CommunityEvent> items, DateTimeOffset now) =>
        items
            .Select(e => NextOccurrence(e, now))
            .Where(e => ParseTime(e.Start) is { } start && EndOf(e, start) > now)
            .OrderBy(e => ParseTime(e.Start))
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>For a weekly event whose listed occurrence is over, the same event shifted by whole weeks to the one running or coming next.</summary>
    public static CommunityEvent NextOccurrence(CommunityEvent item, DateTimeOffset now)
    {
        if (!string.Equals(item.Repeat?.Trim(), "weekly", StringComparison.OrdinalIgnoreCase) || ParseTime(item.Start) is not { } start)
        {
            return item;
        }
        var end = EndOf(item, start);
        if (end > now)
        {
            return item;
        }
        var weeks = (long)Math.Floor((now - end).TotalDays / 7) + 1;
        var shift = TimeSpan.FromDays(7 * weeks);
        return new CommunityEvent
        {
            Id = item.Id,
            Title = item.Title,
            Description = item.Description,
            Start = (start + shift).ToString("o", CultureInfo.InvariantCulture),
            End = ParseTime(item.End) is null ? item.End : (end + shift).ToString("o", CultureInfo.InvariantCulture),
            Repeat = item.Repeat,
            Host = item.Host,
            Location = item.Location,
            Url = item.Url,
            LinkText = item.LinkText,
        };
    }

    /// <summary>The event's end: its own when that is readable and after the start, otherwise start plus <see cref="DefaultLength"/>.</summary>
    public static DateTimeOffset EndOf(CommunityEvent item, DateTimeOffset start) =>
        ParseTime(item.End) is { } end && end > start ? end : start + DefaultLength;

    public static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
