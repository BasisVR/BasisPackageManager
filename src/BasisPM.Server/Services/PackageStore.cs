using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Server.Models;

namespace BasisPM.Server.Services;

public sealed class PackageStore
{
    private static readonly JsonSerializerOptions FileOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<RegistryPackage> _packages;

    public PackageStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "registry.json");

        if (File.Exists(_path))
        {
            try
            {
                var json = File.ReadAllText(_path);
                _packages = JsonSerializer.Deserialize<List<RegistryPackage>>(json, FileOpts) ?? Seed();
            }
            catch
            {
                _packages = Seed();
            }
        }
        else
        {
            _packages = Seed();
            Save();
        }
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
            "downloads" => q.OrderByDescending(p => p.Downloads),
            "stars" => q.OrderByDescending(p => p.Stars),
            "updated" => q.OrderByDescending(p => p.Updated),
            "name" => q.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase),
            _ => q.OrderByDescending(p => p.Stars).ThenByDescending(p => p.Downloads),
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
        if (string.IsNullOrWhiteSpace(sub.GitUrl) && string.IsNullOrWhiteSpace(sub.NuGetId))
            throw new ArgumentException("Provide either a gitUrl (UPM) or a nugetId.");

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
            pkg.Source = string.IsNullOrWhiteSpace(sub.Source)
                ? (string.IsNullOrWhiteSpace(sub.NuGetId) ? "community" : "nuget")
                : sub.Source.Trim().ToLowerInvariant();
            pkg.GitUrl = sub.GitUrl?.Trim();
            pkg.NuGetId = sub.NuGetId?.Trim();
            pkg.RepoUrl = sub.RepoUrl?.Trim();
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
        var catalog = new Catalog { Name = "Basis Package Registry", Url = "" };
        lock (_gate)
        {
            foreach (var p in _packages.Where(p => !string.IsNullOrWhiteSpace(p.GitUrl)))
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
                        },
                    },
                };
            }
        }
        return catalog;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_packages, FileOpts);
        File.WriteAllText(_path, json);
    }

    private static List<RegistryPackage> Seed() => new()
    {
        new RegistryPackage
        {
            Id = "com.basis.framework", Name = "Basis Framework", Icon = "🧩",
            Description = "Core BasisVR networked social framework — avatars, networking, and the runtime that everything else builds on.",
            Author = "BasisVR", AuthorUrl = "https://basisvr.org", Category = "Framework",
            Tags = new() { "core", "networking", "avatars" }, Source = "curated",
            GitUrl = "https://github.com/BasisVR/Basis.git?path=Basis/Packages/com.basis.framework",
            RepoUrl = "https://github.com/BasisVR/Basis", Unity = "6000.0", Version = "0.1.0",
            Downloads = 12_400, Stars = 342, Updated = "2026-06-28",
            Dependencies = new() { ["com.basis.networking"] = "^0.1.0" },
        },
        new RegistryPackage
        {
            Id = "com.basis.networking", Name = "Basis Networking", Icon = "🌐",
            Description = "LiteNetLib-based networking layer for BasisVR — transport, sync codecs, and the reduction pipeline.",
            Author = "BasisVR", AuthorUrl = "https://basisvr.org", Category = "Networking",
            Tags = new() { "networking", "litenetlib", "sync" }, Source = "curated",
            GitUrl = "https://github.com/BasisVR/Basis.git?path=Basis/Packages/com.basis.networking",
            RepoUrl = "https://github.com/BasisVR/Basis", Unity = "6000.0", Version = "0.1.0",
            Downloads = 9_800, Stars = 118, Updated = "2026-06-28",
        },
        new RegistryPackage
        {
            Id = "com.basis.sdk", Name = "Basis SDK", Icon = "🛠️",
            Description = "Authoring SDK for creating BasisVR avatars and worlds, including the editor tooling and build pipeline.",
            Author = "BasisVR", AuthorUrl = "https://basisvr.org", Category = "SDK",
            Tags = new() { "sdk", "editor", "authoring" }, Source = "curated",
            GitUrl = "https://github.com/BasisVR/Basis.git?path=Basis/Packages/com.basis.sdk",
            RepoUrl = "https://github.com/BasisVR/Basis", Unity = "6000.0", Version = "0.1.0",
            Downloads = 8_100, Stars = 96, Updated = "2026-06-28",
            Dependencies = new() { ["com.basis.framework"] = "^0.1.0" },
        },
        new RegistryPackage
        {
            Id = "com.basis.mediapipe", Name = "Basis MediaPipe", Icon = "👁️",
            Description = "Webcam face and hand tracking for desktop avatars via Google MediaPipe.",
            Author = "BasisVR", AuthorUrl = "https://basisvr.org", Category = "Tracking",
            Tags = new() { "tracking", "mediapipe", "face", "webcam" }, Source = "curated",
            GitUrl = "https://github.com/BasisVR/Basis.git?path=Basis/Packages/com.basis.mediapipe",
            RepoUrl = "https://github.com/BasisVR/Basis", Unity = "6000.0", Version = "0.1.0",
            Downloads = 3_200, Stars = 74, Updated = "2026-06-20",
        },
        new RegistryPackage
        {
            Id = "LiteNetLib", Name = "LiteNetLib", Icon = "🔌",
            Description = "Lightweight reliable UDP networking library — the transport BasisVR is built on.",
            Author = "RevenantX", AuthorUrl = "https://github.com/RevenantX/LiteNetLib", Category = "Networking",
            Tags = new() { "udp", "networking", "transport" }, Source = "nuget",
            NuGetId = "LiteNetLib", RepoUrl = "https://github.com/RevenantX/LiteNetLib", Version = "1.3.1",
            Downloads = 2_100_000, Stars = 3100, Updated = "2026-02-14",
        },
        new RegistryPackage
        {
            Id = "Newtonsoft.Json", Name = "Newtonsoft.Json", Icon = "📄",
            Description = "Popular high-performance JSON framework for .NET, widely used in Unity tooling.",
            Author = "James Newton-King", AuthorUrl = "https://www.newtonsoft.com/json", Category = "Serialization",
            Tags = new() { "json", "serialization" }, Source = "nuget",
            NuGetId = "Newtonsoft.Json", RepoUrl = "https://github.com/JamesNK/Newtonsoft.Json", Version = "13.0.3",
            Downloads = 5_400_000_000, Stars = 10800, Updated = "2026-01-30",
        },
        new RegistryPackage
        {
            Id = "MessagePack", Name = "MessagePack", Icon = "📦",
            Description = "Extremely fast MessagePack serializer for C# — great for compact network payloads.",
            Author = "Cysharp", AuthorUrl = "https://github.com/MessagePack-CSharp/MessagePack-CSharp", Category = "Serialization",
            Tags = new() { "serialization", "binary", "performance" }, Source = "nuget",
            NuGetId = "MessagePack", RepoUrl = "https://github.com/MessagePack-CSharp/MessagePack-CSharp", Version = "3.1.4",
            Downloads = 210_000_000, Stars = 5900, Updated = "2026-03-11",
        },
        new RegistryPackage
        {
            Id = "protobuf-net", Name = "protobuf-net", Icon = "🧬",
            Description = "Protocol Buffers serialization for .NET with a contract-based, idiomatic C# API.",
            Author = "Marc Gravell", AuthorUrl = "https://github.com/protobuf-net/protobuf-net", Category = "Serialization",
            Tags = new() { "protobuf", "serialization" }, Source = "nuget",
            NuGetId = "protobuf-net", RepoUrl = "https://github.com/protobuf-net/protobuf-net", Version = "3.2.30",
            Downloads = 160_000_000, Stars = 4700, Updated = "2026-02-02",
        },
        new RegistryPackage
        {
            Id = "K4os.Compression.LZ4", Name = "K4os.Compression.LZ4", Icon = "🗜️",
            Description = "LZ4 compression for .NET — one half of BasisVR's network compression stack.",
            Author = "Milosz Krajewski", AuthorUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4", Category = "Compression",
            Tags = new() { "compression", "lz4", "networking" }, Source = "nuget",
            NuGetId = "K4os.Compression.LZ4", RepoUrl = "https://github.com/MiloszKrajewski/K4os.Compression.LZ4", Version = "1.3.8",
            Downloads = 82_000_000, Stars = 720, Updated = "2025-12-18",
        },
    };
}
