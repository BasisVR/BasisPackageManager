using System.Net;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class AdvisoryService
{
    public const string FileName = "advisories.json";
    public const string DefaultAdvisoriesUrl = "https://basisvr.org/packages/advisories.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly HttpClient _http;
    private readonly string _cachePath;

    public AdvisoryService(HttpClient? http = null, string? cachePath = null)
    {
        _http = http ?? HttpRetryHandler.CreateClient(TimeSpan.FromSeconds(20));
        _cachePath = cachePath ?? Path.Combine(AppDataPaths.Root, FileName);
    }

    public static string AdvisoriesUrlFor(string? catalogUrl)
    {
        var configured = string.IsNullOrWhiteSpace(catalogUrl) ? CatalogService.DefaultCatalogUrl : catalogUrl.Trim();
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri)) return DefaultAdvisoriesUrl;
        var path = uri.AbsolutePath;
        var slash = path.LastIndexOf('/');
        if (!string.Equals(path[(slash + 1)..], "catalog.json", StringComparison.OrdinalIgnoreCase)) return DefaultAdvisoriesUrl;
        return new UriBuilder(uri) { Path = path[..(slash + 1)] + FileName, Query = "" }.Uri.AbsoluteUri;
    }

    public async Task<IReadOnlyList<PackageAdvisory>> LoadAsync(string? catalogUrl, CancellationToken ct = default)
    {
        var url = AdvisoriesUrlFor(catalogUrl);
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Save(url, Array.Empty<PackageAdvisory>());
                return Array.Empty<PackageAdvisory>();
            }
            response.EnsureSuccessStatusCode();
            var list = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            Save(url, list);
            return list;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or JsonException or IOException or NotSupportedException)
        {
            DiagnosticLog.Write($"Loading package advisories from {url}", ex);
            return Cached(url);
        }
    }

    public static IReadOnlyList<PackageAdvisory> Parse(string json) =>
        (JsonSerializer.Deserialize<List<PackageAdvisory?>>(json, JsonOpts) ?? new())
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Package))
            .Select(a => a!)
            .ToList();

    public static PackageAdvisory? Match(IEnumerable<PackageAdvisory>? advisories, string? packageId, string? repository, string? commit, bool unknownCommitMatches = true)
    {
        if (advisories is null || string.IsNullOrWhiteSpace(packageId)) return null;
        commit = string.IsNullOrWhiteSpace(commit) ? null : commit.Trim();
        PackageAdvisory? found = null;
        foreach (var advisory in advisories)
        {
            if (!string.Equals(advisory.Package.Trim(), packageId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(advisory.Repository) && !string.IsNullOrWhiteSpace(repository) && !SameRepository(advisory.Repository, repository)) continue;
            if (advisory.Commits is { Count: > 0 } commits && (commit is null ? !unknownCommitMatches : !commits.Any(c => PackageScanService.SameCommit(c, commit)))) continue;
            if (found is null || advisory.Level > found.Level) found = advisory;
        }
        return found;
    }

    public static (string? Repository, string? Commit) InstalledSource(string unityProjectPath, string packageId, string? manifestValue, string? cloneFolder)
    {
        var sidecar = BasisDevStore.Read(unityProjectPath, packageId);
        var listed = BasisPackagesStore.Find(unityProjectPath, packageId);
        var repository = Blank(sidecar?.Upstream.Url) ?? Blank(listed?.Url) ?? UpmGitUrl.Parse(manifestValue)?.CloneUrl;
        var commit = HeadCommit(cloneFolder) ?? Blank(sidecar?.Upstream.Commit) ?? Blank(listed?.Commit) ?? LockedHash(unityProjectPath, packageId);
        return (repository, commit);
    }

    public static string? HeadCommit(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        var gitDir = Path.Combine(folder, ".git");
        if (!Directory.Exists(gitDir)) return null;
        try
        {
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal)) return IsObjectName(head) ? head : null;
            var name = head[4..].Trim();
            if (!name.StartsWith("refs/", StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)) return null;
            var loose = Path.Combine(gitDir, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose)) return File.ReadAllText(loose).Trim() is var sha && IsObjectName(sha) ? sha : null;
            var packed = Path.Combine(gitDir, "packed-refs");
            if (!File.Exists(packed)) return null;
            foreach (var line in File.ReadLines(packed))
            {
                var space = line.IndexOf(' ');
                if (space > 0 && line[(space + 1)..] == name && IsObjectName(line[..space])) return line[..space];
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? LockedHash(string unityProjectPath, string packageId)
    {
        var path = Path.Combine(unityProjectPath, "Packages", "packages-lock.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object
                && dependencies.TryGetProperty(packageId, out var entry) && entry.ValueKind == JsonValueKind.Object
                && entry.TryGetProperty("hash", out var hash) && hash.ValueKind == JsonValueKind.String && IsObjectName(hash.GetString())
                ? hash.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsObjectName(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static bool SameRepository(string a, string b)
    {
        if (UpmGitUrl.Parse(a) is { } x && UpmGitUrl.Parse(b) is { } y)
            return x.Host.Equals(y.Host, StringComparison.OrdinalIgnoreCase) && x.Owner.Equals(y.Owner, StringComparison.OrdinalIgnoreCase) && x.Repo.Equals(y.Repo, StringComparison.OrdinalIgnoreCase);
        return string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private sealed class CacheFile
    {
        public string Url { get; set; } = "";
        public DateTimeOffset Fetched { get; set; }
        public List<PackageAdvisory> Advisories { get; set; } = new();
    }

    private void Save(string url, IReadOnlyList<PackageAdvisory> advisories)
    {
        try
        {
            if (advisories.Count == 0 && (!File.Exists(_cachePath) || !string.Equals(JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath), JsonOpts)?.Url, url, StringComparison.OrdinalIgnoreCase))) return;
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            AtomicFile.WriteAllText(_cachePath, JsonSerializer.Serialize(new CacheFile { Url = url, Fetched = DateTimeOffset.UtcNow, Advisories = advisories.ToList() }, CacheJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { DiagnosticLog.Write($"Saving package advisories to {_cachePath}", ex); }
    }

    private IReadOnlyList<PackageAdvisory> Cached(string url)
    {
        try
        {
            if (!File.Exists(_cachePath)) return Array.Empty<PackageAdvisory>();
            var cache = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(_cachePath), JsonOpts);
            return cache is not null && string.Equals(cache.Url, url, StringComparison.OrdinalIgnoreCase)
                ? cache.Advisories.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Package)).ToList()
                : Array.Empty<PackageAdvisory>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.Write($"Reading cached package advisories from {_cachePath}", ex);
            return Array.Empty<PackageAdvisory>();
        }
    }
}
