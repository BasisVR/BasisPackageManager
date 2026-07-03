using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>Fetches the registry's bundle feed (bundles.json), the sibling of the package catalog.</summary>
public sealed class BundleService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public BundleService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public const string DefaultBundlesUrl = "https://basisvr.org/packages/bundles.json";

    /// <summary>Derives the bundles URL from a configured catalog URL (…/catalog.json → …/bundles.json).</summary>
    public static string BundlesUrlFor(string? catalogUrl)
    {
        var b = string.IsNullOrWhiteSpace(catalogUrl) ? CatalogService.DefaultCatalogUrl : catalogUrl;
        const string leaf = "catalog.json";
        return b.EndsWith(leaf, StringComparison.OrdinalIgnoreCase)
            ? b[..^leaf.Length] + "bundles.json"
            : DefaultBundlesUrl;
    }

    public async Task<List<Bundle>> LoadAsync(string? catalogUrl, CancellationToken ct = default)
    {
        try
        {
            var list = await _http.GetFromJsonAsync<List<Bundle>>(BundlesUrlFor(catalogUrl), JsonOpts, ct).ConfigureAwait(false);
            if (list is not null) return list;
        }
        catch
        {
        }
        return new List<Bundle>();
    }
}
