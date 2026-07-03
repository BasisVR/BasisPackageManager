using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class UnityReleaseService
{
    private const string BaseUrl = "https://services.api.unity.com/unity/editor/release/v1/releases";
    private const int PageSize = 25;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public UnityReleaseService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<IReadOnlyList<UnityRelease>> FetchAllAsync(string majorVersion = "6000", CancellationToken ct = default)
    {
        var all = new List<UnityRelease>();
        var offset = 0;
        while (true)
        {
            var url = $"{BaseUrl}?limit={PageSize}&offset={offset}&version={majorVersion}";
            var page = await _http.GetFromJsonAsync<UnityReleasePage>(url, JsonOpts, ct).ConfigureAwait(false);
            if (page is null || page.Results is null || page.Results.Count == 0) break;
            all.AddRange(page.Results);
            offset += page.Results.Count;
            if (offset >= page.Total) break;
        }

        return all
            .Select(r => (Release: r, Parsed: UnityVersion.TryParse(r.Version, out var v) ? v : null))
            .OrderByDescending(t => t.Parsed is not null)
            .ThenByDescending(t => t.Parsed)
            .ThenByDescending(t => t.Release.ReleaseDate ?? DateTime.MinValue)
            .Select(t => t.Release)
            .ToList();
    }

    private sealed class UnityReleasePage
    {
        [JsonPropertyName("offset")] public int Offset { get; set; }
        [JsonPropertyName("limit")] public int Limit { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("results")] public List<UnityRelease>? Results { get; set; }
    }
}
