using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class CatalogService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public CatalogService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public async Task<Catalog> LoadAsync(string? url, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var remote = await _http.GetFromJsonAsync<Catalog>(url, JsonOpts, ct).ConfigureAwait(false);
                if (remote is not null) return remote;
            }
            catch
            {
            }
        }

        return LoadEmbedded();
    }

    public static Catalog LoadEmbedded()
    {
        var asm = typeof(CatalogService).Assembly;
        using var stream = asm.GetManifestResourceStream("BasisPM.Core.default-catalog.json");
        if (stream is null) return new Catalog { Name = "Empty" };
        return JsonSerializer.Deserialize<Catalog>(stream, JsonOpts) ?? new Catalog { Name = "Empty" };
    }

    public IEnumerable<CatalogPackageVersion> AllLatest(Catalog catalog)
    {
        foreach (var (_, pkg) in catalog.Packages)
        {
            var latest = pkg.Versions
                .Where(kv => SemVer.TryParse(kv.Key, out _))
                .OrderByDescending(kv => SemVer.Parse(kv.Key))
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(latest.Key))
                yield return latest.Value;
        }
    }

    public CatalogPackageVersion? FindBest(Catalog catalog, string name, SemVerRange range)
    {
        if (!catalog.Packages.TryGetValue(name, out var pkg)) return null;
        return pkg.Versions
            .Where(kv => SemVer.TryParse(kv.Key, out _))
            .Select(kv => (Ver: SemVer.Parse(kv.Key), Entry: kv.Value))
            .Where(t => range.Satisfies(t.Ver))
            .OrderByDescending(t => t.Ver)
            .Select(t => t.Entry)
            .FirstOrDefault();
    }
}
