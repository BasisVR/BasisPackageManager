using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>Fetches the registry's package-list feed (packagelists.json), the sibling of the package catalog.</summary>
public sealed class PackageListService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public PackageListService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }

    public const string DefaultPackageListsUrl = "https://basisvr.org/packages/packagelists.json";

    /// <summary>Id of the published package list used as the minimum "make this a Basis project" set.</summary>
    public const string MinimumPackageListId = "basis-minimum";

    /// <summary>The pre-rename feed name, kept as a fallback so registries that haven't regenerated yet still resolve.</summary>
    public const string LegacyPackageListsUrl = "https://basisvr.org/packages/bundles.json";

    /// <summary>Derives the package-lists URL from a configured catalog URL (…/catalog.json → …/packagelists.json).</summary>
    public static string PackageListsUrlFor(string? catalogUrl) =>
        UrlFor(catalogUrl, "packagelists.json", DefaultPackageListsUrl);

    // The legacy bundles.json URL for the same catalog, tried when the new feed is absent.
    private static string LegacyPackageListsUrlFor(string? catalogUrl) =>
        UrlFor(catalogUrl, "bundles.json", LegacyPackageListsUrl);

    private static string UrlFor(string? catalogUrl, string leafName, string fallback)
    {
        var b = string.IsNullOrWhiteSpace(catalogUrl) ? CatalogService.DefaultCatalogUrl : catalogUrl;
        const string leaf = "catalog.json";
        return b.EndsWith(leaf, StringComparison.OrdinalIgnoreCase)
            ? b[..^leaf.Length] + leafName
            : fallback;
    }

    public async Task<List<PackageList>> LoadAsync(string? catalogUrl, CancellationToken ct = default)
    {
        // Prefer the new feed; fall back to the legacy bundles.json for registries published before the rename.
        var list = await TryFetchAsync(PackageListsUrlFor(catalogUrl), ct).ConfigureAwait(false)
                ?? await TryFetchAsync(LegacyPackageListsUrlFor(catalogUrl), ct).ConfigureAwait(false);
        return list ?? new List<PackageList>();
    }

    private async Task<List<PackageList>?> TryFetchAsync(string url, CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<List<PackageList>>(url, JsonOpts, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
