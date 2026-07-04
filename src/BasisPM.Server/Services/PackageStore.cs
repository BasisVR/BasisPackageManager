using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
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

    // Caps that stop a public submission endpoint being used to fill the disk or push oversized
    // fields to every client. Generous enough for any real package.
    private const int MaxPackages = 5000;
    private const int MaxFieldLen = 2000;
    private const int MaxTags = 24;
    private const int MaxTagLen = 64;
    private const int MaxDependencies = 128;

    /// <summary>
    /// Adds a community-submitted package. Validated and <b>create-only</b>: it will not overwrite an
    /// existing entry, so a submission can never repoint a curated (or any prior) package's git URL.
    /// Real updates go through a pull request to the seed. Throws <see cref="ArgumentException"/> for
    /// bad input and <see cref="InvalidOperationException"/> for a conflicting id / full registry.
    /// </summary>
    public RegistryPackage Upsert(RegistrySubmission sub)
    {
        if (string.IsNullOrWhiteSpace(sub.Id)) throw new ArgumentException("Package id is required.");
        if (string.IsNullOrWhiteSpace(sub.Name)) throw new ArgumentException("Package name is required.");
        if (string.IsNullOrWhiteSpace(sub.GitUrl))
            throw new ArgumentException("Provide a gitUrl (UPM git URL for GitHub or GitLab).");

        var id = sub.Id.Trim();
        if (!IsSafeId(id))
            throw new ArgumentException("Package id may contain only letters, digits, '.', '_' and '-'.");
        if (!GitUrlPolicy.IsHostedGitUrl(sub.GitUrl))
            throw new ArgumentException("gitUrl must be an https URL on github.com or gitlab.com.");
        if (!string.IsNullOrWhiteSpace(sub.RepoUrl) && !GitUrlPolicy.IsWebUrl(sub.RepoUrl))
            throw new ArgumentException("repoUrl must be an http(s) URL.");
        if (!string.IsNullOrWhiteSpace(sub.AuthorUrl) && !GitUrlPolicy.IsWebUrl(sub.AuthorUrl))
            throw new ArgumentException("authorUrl must be an http(s) URL.");
        if (TooLong(sub.Name) || TooLong(sub.Description) || TooLong(sub.Author) || TooLong(sub.Category)
            || TooLong(sub.Version) || TooLong(sub.Unity) || TooLong(sub.GitUrl) || TooLong(sub.RepoUrl) || TooLong(sub.AuthorUrl))
            throw new ArgumentException("One or more fields exceed the length limit.");
        if (sub.Dependencies is { Count: > MaxDependencies })
            throw new ArgumentException("Too many dependencies.");

        lock (_gate)
        {
            if (_packages.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A package with id '{id}' already exists.");
            if (_packages.Count >= MaxPackages)
                throw new InvalidOperationException("The registry is full.");

            var tags = (sub.Tags ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Where(t => t.Length <= MaxTagLen)
                .Take(MaxTags)
                .ToList();

            var pkg = new RegistryPackage
            {
                Id = id,
                Name = sub.Name.Trim(),
                Description = sub.Description?.Trim() ?? "",
                Author = string.IsNullOrWhiteSpace(sub.Author) ? "Community" : sub.Author.Trim(),
                AuthorUrl = sub.AuthorUrl?.Trim(),
                Category = string.IsNullOrWhiteSpace(sub.Category) ? "Misc" : sub.Category.Trim(),
                Tags = tags,
                GitUrl = sub.GitUrl.Trim(),
                RepoUrl = sub.RepoUrl?.Trim(),
                // Always community: "curated" is reserved for the PR-reviewed seed, so a submitter can't
                // mint a trusted-looking badge by pointing at a github.com/BasisVR URL they don't own.
                Source = "community",
                Unity = sub.Unity?.Trim(),
                Version = string.IsNullOrWhiteSpace(sub.Version) ? "1.0.0" : sub.Version.Trim(),
                Dependencies = sub.Dependencies,
                Icon = "📦",
            };

            _packages.Add(pkg);
            Save();
            return pkg;
        }
    }

    private static bool TooLong(string? s) => s is not null && s.Length > MaxFieldLen;

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
            // Kept: an image file the submitter hosts in the exact repo they gave us.
            if (IsOwnRepoImage(p)) continue;
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

    // True only for a raw *image file* that lives in the package's OWN repo — i.e. the image's
    // host + owner/repo (from a raw GitHub/GitLab URL, per the page CSP) matches the package's
    // git/repo URL. Rejects arbitrary remotes, other repos, and non-image files.
    private static bool IsOwnRepoImage(RegistryPackage p)
    {
        var url = p.Image;
        if (string.IsNullOrWhiteSpace(url)) return false;

        var pathOnly = url.Split('?', '#')[0];
        if (!ImageExts.Any(e => pathOnly.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return false;

        var repo = RepoSlug(p.RepoUrl ?? p.GitUrl);
        return repo is not null && string.Equals(repo, ImageRepoSlug(url), StringComparison.OrdinalIgnoreCase);
    }

    // "github|owner|repo" or "gitlab|group/.../repo" from a package git/repo URL; null if unrecognised.
    private static string? RepoSlug(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return null;
        var u = repoUrl.Split('?', '#')[0];
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        u = u.TrimEnd('/');

        var i = u.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var seg = u[(i + "github.com".Length)..].TrimStart('/', ':').Split('/');
            return seg.Length >= 2 ? $"github|{seg[0]}|{seg[1]}".ToLowerInvariant() : null;
        }
        i = u.IndexOf("gitlab.com", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var slug = u[(i + "gitlab.com".Length)..].TrimStart('/', ':');
            return slug.Length > 0 ? $"gitlab|{slug}".ToLowerInvariant() : null;
        }
        return null;
    }

    // Same slug from a strict raw image URL, or null if it isn't one of the two allowed raw hosts.
    private static string? ImageRepoSlug(string url)
    {
        const string gh = "https://raw.githubusercontent.com/";
        const string gl = "https://gitlab.com/";
        if (url.StartsWith(gh, StringComparison.OrdinalIgnoreCase))
        {
            var seg = url[gh.Length..].Split('/');
            return seg.Length >= 2 ? $"github|{seg[0]}|{seg[1]}".ToLowerInvariant() : null;
        }
        if (url.StartsWith(gl, StringComparison.OrdinalIgnoreCase))
        {
            var rest = url[gl.Length..];
            var i = rest.IndexOf("/-/raw/", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? $"gitlab|{rest[..i]}".ToLowerInvariant() : null;
        }
        return null;
    }

    private static List<RegistryPackage>? Read(string path) =>
        JsonSerializer.Deserialize<List<RegistryPackage>>(File.ReadAllText(path), FileOpts);

    private void Save()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_packages, FileOpts));
    }
}
