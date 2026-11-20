using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class BasisPackagesStore
{
    public const string FileName = BpmStore.ManifestFileName;

    private static readonly JsonDocumentOptions PackageJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FilePath(string unityProjectPath) => BpmStore.ManifestPath(unityProjectPath);

    public static IReadOnlyDictionary<string, BasisPackageEntry> Read(string unityProjectPath)
    {
        var result = new Dictionary<string, BasisPackageEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manifest = BpmStore.ReadManifest(unityProjectPath);
            var locked = BpmStore.ReadLock(unityProjectPath);
            foreach (var (id, package) in manifest.Packages)
                if (BasisDevStore.IsValidId(id) && package.IsEnabled && ToEntry(package, Find(locked.Packages, id)) is { } entry) result.TryAdd(id, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { DiagnosticLog.Write($"Reading {FilePath(unityProjectPath)}", ex); }
        return result;
    }

    public static BasisPackageEntry? Find(string unityProjectPath, string packageId) => Read(unityProjectPath).GetValueOrDefault(packageId);

    public static void Set(string unityProjectPath, string packageId, BasisPackageEntry entry)
    {
        if (!BasisDevStore.IsValidId(packageId)) throw new ArgumentException($"\"{packageId}\" can't be listed in {FileName}.");
        BpmStore.Update(unityProjectPath, (manifest, locked) =>
        {
            var url = entry.Url.Trim();
            var existing = Key(manifest.Packages, packageId) is { } key ? manifest.Packages[key] : null;
            var existingLock = Key(locked.Packages, packageId) is { } lockKey ? locked.Packages[lockKey] : null;
            Remove(manifest.Packages, packageId);
            Remove(locked.Packages, packageId);
            var extra = entry.Extra ?? existing?.Extra;
            var requested = BpmVersion.Git(url, entry.Path, Blank(entry.Ref));
            manifest.Packages[packageId] = new BpmManifestPackage { Version = requested, Enabled = existing?.Enabled, CopiedFrom = existing?.CopiedFrom, ForkOf = Blank(entry.ForkOf), Extra = extra };
            locked.Packages[packageId] = new BpmLockPackage
            {
                Version = BpmVersion.Git(url, entry.Path, Blank(entry.Commit) ?? Blank(entry.Ref)),
                Requested = requested,
                PackageVersion = Blank(entry.Version),
                ForkOf = Blank(entry.ForkOf),
                Depth = existingLock?.Depth ?? 0,
                Dependencies = existingLock?.Dependencies,
            };
        });
    }

    public static bool Remove(string unityProjectPath, string packageId)
    {
        if (!File.Exists(FilePath(unityProjectPath)) && !File.Exists(BpmStore.LegacyPath(unityProjectPath))) return false;
        var removed = false;
        BpmStore.Update(unityProjectPath, (manifest, locked) =>
        {
            removed = Remove(manifest.Packages, packageId);
            Remove(locked.Packages, packageId);
        });
        return removed;
    }

    internal static BasisPackageEntry? ToEntry(BpmManifestPackage package, BpmLockPackage? locked)
    {
        var wanted = BpmVersion.Parse(package.Version);
        if (wanted.Kind != BpmVersionKind.Git || string.IsNullOrWhiteSpace(wanted.Url)) return null;
        var pinned = locked is null ? null : BpmVersion.Parse(locked.Version);
        var commit = pinned is { Kind: BpmVersionKind.Git } && BpmVersion.IsCommitHash(pinned.Ref) ? pinned.Ref : null;
        return new BasisPackageEntry
        {
            Version = locked?.PackageVersion,
            Url = wanted.Url,
            Ref = wanted.Ref,
            Path = wanted.SubPath,
            Commit = commit,
            ForkOf = Blank(package.ForkOf) ?? Blank(locked?.ForkOf),
            Extra = package.Extra,
        };
    }

    public static BasisPackageEntry EntryFor(BasisDevUpstream upstream, string? packageRoot) => new()
    {
        Version = ReadVersion(packageRoot),
        Url = upstream.Url,
        Ref = string.IsNullOrWhiteSpace(upstream.Ref) ? null : upstream.Ref,
        Path = string.IsNullOrWhiteSpace(upstream.Path) ? null : upstream.Path,
        Commit = string.IsNullOrWhiteSpace(upstream.Commit) ? null : upstream.Commit,
        ForkOf = string.IsNullOrWhiteSpace(upstream.ForkOf) ? null : upstream.ForkOf,
    };

    public static BasisDevSidecar? SidecarFor(string packageId, BasisPackageEntry entry) => SidecarFor(null, packageId, entry);

    public static BasisDevSidecar? SidecarFor(string? unityProjectPath, string packageId, BasisPackageEntry entry)
    {
        if (!BasisDevStore.IsValidId(packageId) || string.IsNullOrWhiteSpace(entry.Url) || !GitUrlPolicy.IsSafeRef(entry.Ref) || !GitUrlPolicy.IsSafeSubPath(entry.Path)) return null;
        var upstream = new BasisDevUpstream { Url = entry.Url.Trim(), Ref = entry.Ref, Path = entry.Path, Commit = entry.Commit, ForkOf = string.IsNullOrWhiteSpace(entry.ForkOf) ? null : entry.ForkOf.Trim() };
        string folder, mounted;
        if (unityProjectPath is null)
        {
            folder = BasisDevStore.FolderName + "/" + packageId;
            mounted = "file:../" + folder + (string.IsNullOrEmpty(entry.Path) ? "" : "/" + entry.Path);
        }
        else
        {
            var clone = BasisDevStore.CloneFolder(unityProjectPath, packageId);
            folder = BasisDevStore.RelativeFolder(unityProjectPath, clone);
            mounted = BasisDevStore.ManifestLine(unityProjectPath, string.IsNullOrEmpty(entry.Path) ? clone : Path.Combine(clone, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
        }
        return new BasisDevSidecar
        {
            Package = packageId,
            Folder = folder,
            Upstream = upstream,
            Manifest = new BasisDevManifestLine { Original = upstream.ManifestUrl, Mounted = mounted },
        };
    }

    public static BasisDevSidecar? SidecarFor(string unityProjectPath, string packageId) =>
        Find(unityProjectPath, packageId) is { } entry ? SidecarFor(unityProjectPath, packageId, entry) : null;

    public static IReadOnlyList<BasisDevSidecar> Sidecars(string unityProjectPath) =>
        Read(unityProjectPath).Select(p => SidecarFor(unityProjectPath, p.Key, p.Value)).OfType<BasisDevSidecar>().ToList();

    public static string? ReadVersion(string? packageRoot)
    {
        if (string.IsNullOrEmpty(packageRoot)) return null;
        try
        {
            var path = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path), PackageJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String
                && version.GetString() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string? Key<T>(Dictionary<string, T> map, string packageId) =>
        map.Keys.FirstOrDefault(k => string.Equals(k, packageId, StringComparison.OrdinalIgnoreCase));

    private static T? Find<T>(Dictionary<string, T> map, string packageId) where T : class => Key(map, packageId) is { } key ? map[key] : null;

    private static bool Remove<T>(Dictionary<string, T> map, string packageId) => Key(map, packageId) is { } key && map.Remove(key);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
