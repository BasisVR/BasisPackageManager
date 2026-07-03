using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Server.Models;

namespace BasisPM.Server.Services;

public sealed class PackageStore
{
    private static readonly JsonSerializerOptions FileOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<RegistryPackage> _packages;

    // Canonical package data lives in seed/packages.json (committed, PR-editable).
    // Runtime submissions on a live server are written to dataDir/registry.json on top of the seed.
    public PackageStore(string dataDir, string? seedPath = null)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "registry.json");

        if (File.Exists(_path))
        {
            try { _packages = Read(_path) ?? LoadSeed(seedPath); }
            catch { _packages = LoadSeed(seedPath); }
        }
        else
        {
            _packages = LoadSeed(seedPath);
            Save();
        }
    }

    public static List<RegistryPackage> LoadSeed(string? seedPath)
    {
        if (!string.IsNullOrEmpty(seedPath) && File.Exists(seedPath))
        {
            try
            {
                var list = Read(seedPath);
                if (list is { Count: > 0 })
                {
                    foreach (var p in list) p.Source = DeriveSource(p.RepoUrl, p.GitUrl);
                    return list;
                }
            }
            catch { }
        }
        return new List<RegistryPackage>();
    }

    /// <summary>Curated = hosted under github.com/BasisVR; anything else is community.</summary>
    public static string DeriveSource(string? repoUrl, string? gitUrl)
    {
        var u = (repoUrl ?? gitUrl ?? "").ToLowerInvariant();
        var idx = u.IndexOf("github.com", StringComparison.Ordinal);
        if (idx < 0) return "community";
        var owner = u[(idx + "github.com".Length)..].TrimStart('/', ':')
            .Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(owner, "basisvr", StringComparison.OrdinalIgnoreCase) ? "curated" : "community";
    }

    public IReadOnlyList<RegistryPackage> All()
    {
        lock (_gate) return _packages.ToList();
    }

    public RegistryPackage? Get(string id)
    {
        lock (_gate) return _packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<RegistryPackage> Query(string? search, string? source, string? category, string? sort)
    {
        IEnumerable<RegistryPackage> q;
        lock (_gate) q = _packages.ToList();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(p =>
                p.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Id.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Description.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Author.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                p.Tags.Any(t => t.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }
        if (!string.IsNullOrWhiteSpace(source) && !source.Equals("all", StringComparison.OrdinalIgnoreCase))
            q = q.Where(p => p.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(category) && !category.Equals("all", StringComparison.OrdinalIgnoreCase))
            q = q.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

        q = sort switch
        {
            "forks" => q.OrderByDescending(p => p.Forks),
            "stars" => q.OrderByDescending(p => p.Stars),
            "updated" => q.OrderByDescending(p => p.Updated),
            "name" => q.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(p => p.Stars).ThenByDescending(p => p.Forks),
        };

        return q.ToList();
    }

    public IReadOnlyList<string> Categories()
    {
        lock (_gate)
            return _packages.Select(p => p.Category).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public RegistryPackage Upsert(RegistrySubmission sub)
    {
        if (string.IsNullOrWhiteSpace(sub.Id)) throw new ArgumentException("Package id is required.");
        if (string.IsNullOrWhiteSpace(sub.Name)) throw new ArgumentException("Package name is required.");
        if (string.IsNullOrWhiteSpace(sub.GitUrl))
            throw new ArgumentException("Provide a gitUrl (UPM git URL for GitHub or GitLab).");

        lock (_gate)
        {
            var existing = _packages.FirstOrDefault(p => string.Equals(p.Id, sub.Id, StringComparison.OrdinalIgnoreCase));
            var pkg = existing ?? new RegistryPackage { Id = sub.Id.Trim() };

            pkg.Name = sub.Name.Trim();
            pkg.Description = sub.Description?.Trim() ?? "";
            pkg.Author = string.IsNullOrWhiteSpace(sub.Author) ? "Community" : sub.Author.Trim();
            pkg.AuthorUrl = sub.AuthorUrl;
            pkg.Category = string.IsNullOrWhiteSpace(sub.Category) ? "Misc" : sub.Category.Trim();
            pkg.Tags = sub.Tags ?? pkg.Tags;
            pkg.GitUrl = sub.GitUrl?.Trim();
            pkg.RepoUrl = sub.RepoUrl?.Trim();
            pkg.Source = DeriveSource(pkg.RepoUrl, pkg.GitUrl);
            pkg.Unity = sub.Unity?.Trim();
            pkg.Version = string.IsNullOrWhiteSpace(sub.Version) ? (existing?.Version ?? "1.0.0") : sub.Version.Trim();
            if (string.IsNullOrEmpty(pkg.Icon)) pkg.Icon = "📦";

            if (existing is null) _packages.Add(pkg);
            Save();
            return pkg;
        }
    }

    public Catalog ToCatalog()
    {
        lock (_gate) return BuildCatalog(_packages);
    }

    public static Catalog BuildCatalog(IEnumerable<RegistryPackage> packages)
    {
        var catalog = new Catalog { Name = "Basis Package Registry", Url = "" };
        foreach (var p in packages.Where(p => !string.IsNullOrWhiteSpace(p.GitUrl)))
        {
            catalog.Packages[p.Id] = new CatalogPackage
            {
                Versions = new Dictionary<string, CatalogPackageVersion>
                {
                    [p.Version] = new CatalogPackageVersion
                    {
                        Name = p.Id,
                        DisplayName = p.Name,
                        Version = p.Version,
                        Description = p.Description,
                        Unity = p.Unity,
                        Url = p.GitUrl,
                        Dependencies = p.Dependencies,
                        Author = new CatalogAuthor { Name = p.Author, Url = p.AuthorUrl },
                        Image = p.Image,
                    },
                },
            };
        }
        return catalog;
    }

    private static readonly string[] ImageExts = { ".png", ".webp", ".jpg", ".jpeg", ".gif" };

    /// <summary>
    /// Resolves each package's <see cref="RegistryPackage.Image"/>: a raw image URL hosted in the
    /// package's own repo (GitHub/GitLab, matching the page CSP) is kept as-is; otherwise it falls
    /// back to a self-hosted <c>{iconsDir}/{id}.{ext}</c>, or null. Arbitrary remotes are dropped.
    /// </summary>
    public static void ResolveImages(IEnumerable<RegistryPackage> packages, string iconsDir)
    {
        var haveDir = Directory.Exists(iconsDir);
        foreach (var p in packages)
        {
            // Kept: an image the submitter hosts in the repo they gave us.
            if (IsRepoImageUrl(p.Image)) continue;
            p.Image = null;
            if (!haveDir || !IsSafeId(p.Id)) continue;
            foreach (var ext in ImageExts)
            {
                if (File.Exists(Path.Combine(iconsDir, p.Id + ext)))
                {
                    p.Image = "icons/" + p.Id + ext;
                    break;
                }
            }
        }
    }

    public void ResolveImages(string iconsDir)
    {
        lock (_gate) ResolveImages(_packages, iconsDir);
    }

    // Package ids only (e.g. com.foo.bar) — blocks path traversal when building the icon filename.
    private static bool IsSafeId(string id) =>
        !string.IsNullOrEmpty(id) && id.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    // A raw file URL on a host the page CSP permits — not an arbitrary remote.
    private static bool IsRepoImageUrl(string? url) =>
        !string.IsNullOrEmpty(url) &&
        (url.StartsWith("https://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
         (url.StartsWith("https://gitlab.com/", StringComparison.OrdinalIgnoreCase) && url.Contains("/-/raw/", StringComparison.OrdinalIgnoreCase)));

    private static List<RegistryPackage>? Read(string path) =>
        JsonSerializer.Deserialize<List<RegistryPackage>>(File.ReadAllText(path), FileOpts);

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_packages, FileOpts));
    }
}
