using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class BasisDevStore
{
    public const string FolderName = ".basisdev";
    public const string SidecarExtension = ".json";

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

    public static string ToolName { get; } = "BasisPM " + (typeof(BasisDevStore).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    public static string Root(string unityProjectPath) => Path.Combine(unityProjectPath, FolderName);

    public static string CloneFolder(string unityProjectPath, string packageId) => Path.Combine(Root(unityProjectPath), packageId);

    public static string SidecarPath(string unityProjectPath, string packageId) => Path.Combine(Root(unityProjectPath), packageId + SidecarExtension);

    public static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 214 && id[0] != '.' && id.Trim() == id
        && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

    public static BasisDevSidecar? Read(string unityProjectPath, string packageId)
    {
        if (!IsValidId(packageId)) return null;
        var path = SidecarPath(unityProjectPath, packageId);
        return File.Exists(path) ? Load(path, packageId) : null;
    }

    public static IReadOnlyList<BasisDevSidecar> List(string unityProjectPath)
    {
        var result = new List<BasisDevSidecar>();
        var root = Root(unityProjectPath);
        if (!Directory.Exists(root)) return result;
        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*" + SidecarExtension, SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                if (IsValidId(id) && Load(path, id) is { } sidecar) result.Add(sidecar);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing .basisdev sidecars in {root}", ex); }
        result.Sort((a, b) => string.Compare(a.Package, b.Package, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    public static IReadOnlyDictionary<string, string> ListClones(string unityProjectPath)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Root(unityProjectPath);
        if (!Directory.Exists(root)) return result;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(folder);
                if (IsValidId(name) && MountService.IsWorkingClone(folder)) result[name] = folder;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing .basisdev clones in {root}", ex); }
        return result;
    }

    public static void Write(string unityProjectPath, BasisDevSidecar sidecar)
    {
        if (!IsValidId(sidecar.Package)) throw new ArgumentException($"\"{sidecar.Package}\" can't be used as a .basisdev sidecar name.");
        if (ResolveFolder(unityProjectPath, sidecar.Folder) is null) throw new ArgumentException($"\"{sidecar.Folder}\" isn't a folder inside the project's Packages or {FolderName} folder.");
        sidecar.Format = BasisDevSidecar.CurrentFormat;
        AtomicFile.WriteAllText(SidecarPath(unityProjectPath, sidecar.Package), JsonSerializer.Serialize(sidecar, Json) + "\n");
    }

    public static bool Delete(string unityProjectPath, string packageId)
    {
        if (!IsValidId(packageId)) return false;
        var path = SidecarPath(unityProjectPath, packageId);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public static string RelativeFolder(string unityProjectPath, string folder) =>
        Path.GetRelativePath(Path.GetFullPath(unityProjectPath), Path.GetFullPath(folder)).Replace('\\', '/').TrimEnd('/');

    public static string? ResolveFolder(string unityProjectPath, string? relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder) || !GitUrlPolicy.IsSafeSubPath(relativeFolder)) return null;
        var parts = relativeFolder.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts.Any(p => p == ".") || !IsValidId(parts[1])) return null;
        if (!string.Equals(parts[0], FolderName, StringComparison.Ordinal) && !string.Equals(parts[0], "Packages", StringComparison.Ordinal)) return null;
        return Path.Combine(Path.GetFullPath(unityProjectPath), parts[0], parts[1]);
    }

    public static string? ResolveFolder(string unityProjectPath, BasisDevSidecar sidecar) => ResolveFolder(unityProjectPath, sidecar.Folder);

    public static string? PackageRoot(string cloneFolder, string? subPath)
    {
        if (!GitUrlPolicy.IsSafeSubPath(subPath)) return null;
        var root = string.IsNullOrEmpty(subPath) ? cloneFolder : Path.Combine(cloneFolder, subPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(Path.Combine(root, "package.json")) ? root : null;
    }

    private static BasisDevSidecar? Load(string path, string packageId)
    {
        try
        {
            var sidecar = JsonSerializer.Deserialize<BasisDevSidecar>(File.ReadAllText(path), Json);
            if (sidecar is null) return null;
            if (string.IsNullOrWhiteSpace(sidecar.Package)) sidecar.Package = packageId;
            if (!string.Equals(sidecar.Package, packageId, StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write($"Reading .basisdev sidecar {path}", new InvalidDataException($"It describes {sidecar.Package}, not {packageId}."));
                return null;
            }
            sidecar.Upstream ??= new BasisDevUpstream();
            sidecar.Manifest ??= new BasisDevManifestLine();
            return sidecar;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading .basisdev sidecar {path}", ex);
            if (ex is JsonException) AtomicFile.KeepUnreadableCopy(path);
            return null;
        }
    }
}
