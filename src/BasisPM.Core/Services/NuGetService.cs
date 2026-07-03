using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class NuGetService
{
    private const string SearchEndpoint = "https://azuresearch-usnc.nuget.org/query";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public NuGetService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisPackageManager/1.0");
    }

    public async Task<IReadOnlyList<NuGetPackage>> SearchAsync(string query, bool includePrerelease = false, int take = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<NuGetPackage>();
        var url = $"{SearchEndpoint}?q={Uri.EscapeDataString(query)}&take={take}&prerelease={(includePrerelease ? "true" : "false")}&semVerLevel=2.0.0";
        var response = await _http.GetFromJsonAsync<NuGetSearchResponse>(url, JsonOpts, ct).ConfigureAwait(false);
        return response?.Data ?? new List<NuGetPackage>();
    }

    public static string PackagesConfigPath(string unityProjectPath) =>
        Path.Combine(unityProjectPath, "Assets", "packages.config");

    public IReadOnlyList<NuGetInstalled> ReadInstalled(string unityProjectPath)
    {
        var path = PackagesConfigPath(unityProjectPath);
        if (!File.Exists(path)) return Array.Empty<NuGetInstalled>();
        try
        {
            var doc = XDocument.Load(path);
            return doc.Root?
                .Elements("package")
                .Select(e => new NuGetInstalled(
                    (string?)e.Attribute("id") ?? "",
                    (string?)e.Attribute("version") ?? ""))
                .Where(p => p.Id.Length > 0)
                .ToList() ?? new List<NuGetInstalled>();
        }
        catch
        {
            return Array.Empty<NuGetInstalled>();
        }
    }

    public void AddOrUpdate(string unityProjectPath, string id, string version)
    {
        var path = PackagesConfigPath(unityProjectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        XDocument doc;
        if (File.Exists(path))
        {
            try { doc = XDocument.Load(path); }
            catch { doc = NewDocument(); }
        }
        else
        {
            doc = NewDocument();
        }

        var root = doc.Root;
        if (root is null)
        {
            root = new XElement("packages");
            doc.Add(root);
        }
        var existing = root.Elements("package")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), id, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            root.Add(new XElement("package",
                new XAttribute("id", id),
                new XAttribute("version", version),
                new XAttribute("manuallyInstalled", "true")));
        }
        else
        {
            existing.SetAttributeValue("version", version);
            existing.SetAttributeValue("manuallyInstalled", "true");
        }

        Save(doc, path);
    }

    public bool Remove(string unityProjectPath, string id)
    {
        var path = PackagesConfigPath(unityProjectPath);
        if (!File.Exists(path)) return false;
        XDocument doc;
        try { doc = XDocument.Load(path); }
        catch { return false; }

        var target = doc.Root?
            .Elements("package")
            .FirstOrDefault(e => string.Equals((string?)e.Attribute("id"), id, StringComparison.OrdinalIgnoreCase));
        if (target is null) return false;

        target.Remove();
        Save(doc, path);
        return true;
    }

    private static XDocument NewDocument() =>
        new(new XDeclaration("1.0", "utf-8", null), new XElement("packages"));

    private static void Save(XDocument doc, string path)
    {
        using var writer = new StreamWriter(path, append: false, System.Text.Encoding.UTF8);
        doc.Save(writer);
    }
}
