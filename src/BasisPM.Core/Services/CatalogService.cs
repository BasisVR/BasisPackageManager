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

    /// <summary>The Basis package registry (basisvr.org/packages) — used when no catalog URL is configured.</summary>
    public const string DefaultCatalogUrl = "https://basisvr.org/packages/catalog.json";

    public async Task<Catalog> LoadAsync(string? url, CancellationToken ct = default)
    {
        var effective = string.IsNullOrWhiteSpace(url) ? DefaultCatalogUrl : url;
        return await FetchAsync(effective, ct).ConfigureAwait(false) ?? LoadEmbedded();
    }

    /// <summary>
    /// Loads a catalog from a URL WITHOUT the embedded-Basis fallback — returns null if it can't be
    /// fetched or parsed. Used for extra (unofficial) catalogs so a broken URL contributes nothing
    /// (rather than silently injecting the bundled Basis catalog as if it were the extra source).
    /// </summary>
    public Task<Catalog?> TryLoadAsync(string url, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(url) ? Task.FromResult<Catalog?>(null) : FetchAsync(url, ct);

    /// <summary>
    /// GETs the catalog JSON and deserializes it, tolerating any content-type — many hosts
    /// (e.g. raw.githubusercontent.com) serve JSON as text/plain, which GetFromJsonAsync rejects.
    /// Returns null on any network or parse failure.
    /// </summary>
    private async Task<Catalog?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<Catalog>(json, JsonOpts);
        }
        catch { return null; }
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
