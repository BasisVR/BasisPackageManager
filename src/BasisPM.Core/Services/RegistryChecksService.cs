using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class RegistryChecksService
{
    public const string FileName = "checks.json";

    private readonly HttpClient _http;

    public RegistryChecksService(HttpClient? http = null)
    {
        _http = http ?? HttpRetryHandler.CreateClient(TimeSpan.FromSeconds(20));
    }

    public static string? UrlFor(string? catalogUrl)
    {
        var source = string.IsNullOrWhiteSpace(catalogUrl) ? CatalogService.DefaultCatalogUrl : catalogUrl.Trim();
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        return new Uri(uri, FileName).ToString();
    }

    public async Task<RegistryChecksFile?> TryLoadAsync(string? catalogUrl, CancellationToken ct = default)
    {
        if (UrlFor(catalogUrl) is not { } url) return null;
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(json);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            DiagnosticLog.Write($"Loading the registry checks from {url}", ex);
            return null;
        }
    }

    public static RegistryChecksFile? Parse(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<RegistryChecksFile>(json, PackageCheckStore.Json);
            if (file is null) return null;
            file.Basis = new Dictionary<string, BasisCheckTarget>(file.Basis ?? new(), StringComparer.OrdinalIgnoreCase);
            var packages = new Dictionary<string, Dictionary<string, PackageCheckState>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, branches) in file.Packages ?? new())
                if (branches is not null) packages[id] = new Dictionary<string, PackageCheckState>(branches.Where(b => b.Value is not null), StringComparer.OrdinalIgnoreCase);
            file.Packages = packages;
            return file;
        }
        catch (JsonException ex)
        {
            DiagnosticLog.Write("Parsing the registry checks", ex);
            return null;
        }
    }

    public static PackageCheckState? Find(RegistryChecksFile? file, string packageId, string? basisBranch)
    {
        if (file is null || !file.Packages.TryGetValue(packageId, out var branches)) return null;
        return BasisChannels.FindRecordKey(branches.Keys, basisBranch) is { } key ? branches[key] : null;
    }

    public static BasisCheckTarget? Head(RegistryChecksFile? file, string? basisBranch) =>
        file is null ? null : BasisChannels.FindRecordKey(file.Basis.Keys, basisBranch) is { } key ? file.Basis[key] : null;

    public static void Set(RegistryChecksFile file, string packageId, string basisBranch, PackageCheckState state, BasisCheckTarget? head = null)
    {
        if (!file.Packages.TryGetValue(packageId, out var branches)) file.Packages[packageId] = branches = new Dictionary<string, PackageCheckState>(StringComparer.OrdinalIgnoreCase);
        branches[basisBranch] = state;
        if (head is not null) file.Basis[basisBranch] = head;
        file.Generated = DateTimeOffset.UtcNow;
    }

    public static string Serialize(RegistryChecksFile file)
    {
        var ordered = new RegistryChecksFile
        {
            Format = RegistryChecksFile.CurrentFormat,
            Generated = file.Generated,
            Basis = new Dictionary<string, BasisCheckTarget>(file.Basis.OrderBy(kv => kv.Key, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase),
            Packages = new Dictionary<string, Dictionary<string, PackageCheckState>>(file.Packages.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, new Dictionary<string, PackageCheckState>(kv.Value.OrderBy(b => b.Key, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase),
        };
        return JsonSerializer.Serialize(ordered, PackageCheckStore.Json).Replace("\r\n", "\n") + "\n";
    }
}
