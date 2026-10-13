using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class BasisPackagesStore
{
    public const string FileName = "basispackages.json";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions PackageJson = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string FilePath(string unityProjectPath) => Path.Combine(unityProjectPath, "Packages", FileName);

    public static IReadOnlyDictionary<string, BasisPackageEntry> Read(string unityProjectPath)
    {
        var result = new Dictionary<string, BasisPackageEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var (id, entry) in Load(FilePath(unityProjectPath)).Packages)
                if (entry is not null && BasisDevStore.IsValidId(id) && !string.IsNullOrWhiteSpace(entry.Url)) result.TryAdd(id, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { DiagnosticLog.Write($"Reading {FilePath(unityProjectPath)}", ex); }
        return result;
    }

    public static BasisPackageEntry? Find(string unityProjectPath, string packageId) => Read(unityProjectPath).GetValueOrDefault(packageId);

    public static void Set(string unityProjectPath, string packageId, BasisPackageEntry entry)
    {
        if (!BasisDevStore.IsValidId(packageId)) throw new ArgumentException($"\"{packageId}\" can't be listed in {FileName}.");
        lock (Gate)
        {
            var path = FilePath(unityProjectPath);
            var file = Load(path);
            if (Key(file, packageId) is { } existing)
            {
                entry.Extra ??= file.Packages[existing]?.Extra;
                file.Packages.Remove(existing);
            }
            file.Packages[packageId] = entry;
            Save(path, file);
        }
    }

    public static bool Remove(string unityProjectPath, string packageId)
    {
        lock (Gate)
        {
            var path = FilePath(unityProjectPath);
            if (!File.Exists(path)) return false;
            var file = Load(path);
            if (Key(file, packageId) is not { } existing) return false;
            file.Packages.Remove(existing);
            Save(path, file);
            return true;
        }
    }

    public static BasisPackageEntry EntryFor(BasisDevUpstream upstream, string? packageRoot) => new()
    {
        Version = ReadVersion(packageRoot),
        Url = upstream.Url,
        Ref = string.IsNullOrWhiteSpace(upstream.Ref) ? null : upstream.Ref,
        Path = string.IsNullOrWhiteSpace(upstream.Path) ? null : upstream.Path,
        Commit = string.IsNullOrWhiteSpace(upstream.Commit) ? null : upstream.Commit,
    };

    public static BasisDevSidecar? SidecarFor(string packageId, BasisPackageEntry entry)
    {
        if (!BasisDevStore.IsValidId(packageId) || string.IsNullOrWhiteSpace(entry.Url) || !GitUrlPolicy.IsSafeRef(entry.Ref) || !GitUrlPolicy.IsSafeSubPath(entry.Path)) return null;
        var upstream = new BasisDevUpstream { Url = entry.Url.Trim(), Ref = entry.Ref, Path = entry.Path, Commit = entry.Commit };
        var folder = BasisDevStore.FolderName + "/" + packageId;
        return new BasisDevSidecar
        {
            Package = packageId,
            Folder = folder,
            Upstream = upstream,
            Manifest = new BasisDevManifestLine { Original = upstream.ManifestUrl, Mounted = "file:../" + folder + (string.IsNullOrEmpty(entry.Path) ? "" : "/" + entry.Path) },
        };
    }

    public static BasisDevSidecar? SidecarFor(string unityProjectPath, string packageId) =>
        Find(unityProjectPath, packageId) is { } entry ? SidecarFor(packageId, entry) : null;

    public static IReadOnlyList<BasisDevSidecar> Sidecars(string unityProjectPath) =>
        Read(unityProjectPath).Select(p => SidecarFor(p.Key, p.Value)).OfType<BasisDevSidecar>().ToList();

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

    private static string? Key(BasisPackagesFile file, string packageId) =>
        file.Packages.Keys.FirstOrDefault(k => string.Equals(k, packageId, StringComparison.OrdinalIgnoreCase));

    private static BasisPackagesFile Load(string path)
    {
        if (!File.Exists(path)) return new BasisPackagesFile();
        BasisPackagesFile? file;
        try { file = JsonSerializer.Deserialize<BasisPackagesFile>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidDataException($"{path} isn't valid JSON ({ex.Message}). Fix it, for example a merge conflict, then try again.", ex); }
        if (file is null) throw new InvalidDataException($"{path} doesn't hold a package list. Fix or delete it, then try again.");
        file.Packages ??= new Dictionary<string, BasisPackageEntry>();
        return file;
    }

    private static void Save(string path, BasisPackagesFile file)
    {
        if (file.Packages.Count == 0 && file.Extra is not { Count: > 0 })
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        file.Format = BasisPackagesFile.CurrentFormat;
        file.Packages = file.Packages.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var text = JsonSerializer.Serialize(file, Json) + "\n";
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal)) return;
        AtomicFile.WriteAllText(path, text);
    }
}
