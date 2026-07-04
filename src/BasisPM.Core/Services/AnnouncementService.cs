using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>
/// Fetches the project's announcement feed (announcements.json) hosted on the static website.
/// Prefers the live feed; falls back to an embedded copy when the network is unavailable.
/// </summary>
public sealed class AnnouncementService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public AnnouncementService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    /// <summary>Where announcements are posted — a hand-edited JSON file at the website root.</summary>
    public const string DefaultAnnouncementsUrl = "https://basisvr.org/announcements.json";

    public async Task<List<Announcement>> LoadAsync(string? url = null, CancellationToken ct = default)
    {
        var effective = string.IsNullOrWhiteSpace(url) ? DefaultAnnouncementsUrl : url;
        try
        {
            var list = await _http.GetFromJsonAsync<List<Announcement>>(effective, JsonOpts, ct).ConfigureAwait(false);
            if (list is not null) return Order(list);
        }
        catch
        {
        }
        return Order(LoadEmbedded());
    }

    public static List<Announcement> LoadEmbedded()
    {
        var asm = typeof(AnnouncementService).Assembly;
        using var stream = asm.GetManifestResourceStream("BasisPM.Core.announcements.json");
        if (stream is null) return new List<Announcement>();
        return JsonSerializer.Deserialize<List<Announcement>>(stream, JsonOpts) ?? new List<Announcement>();
    }

    /// <summary>Pinned first, then newest date first, then by id for a stable order.</summary>
    private static List<Announcement> Order(List<Announcement> items) =>
        items
            .OrderByDescending(a => a.Pinned)
            .ThenByDescending(a => ParseDate(a.Date))
            .ThenByDescending(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static DateTimeOffset ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : DateTimeOffset.MinValue;
}
