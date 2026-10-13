using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class UnityPackageMap
{
    public const string AssetsId = "Assets";

    public static IReadOnlyList<ProjectPackageRef> List(string unityProjectPath, bool includeAssets = true)
    {
        var found = new Dictionary<string, ProjectPackageRef>(StringComparer.OrdinalIgnoreCase);
        var packages = Path.Combine(unityProjectPath, "Packages");
        if (Directory.Exists(packages))
            foreach (var dir in SafeDirectories(packages))
                if (ReadName(dir) is { } name) found.TryAdd(name, new ProjectPackageRef(name, Path.GetFullPath(dir), PackageOrigin.Embedded));

        var manifest = ReadDependencies(Path.Combine(packages, "manifest.json"));
        foreach (var (id, value) in manifest)
        {
            if (found.ContainsKey(id) || LocalFolder(unityProjectPath, value) is not { } folder) continue;
            found[id] = new ProjectPackageRef(id, folder, PackageOrigin.Local);
        }

        var cache = Path.Combine(unityProjectPath, "Library", "PackageCache");
        if (Directory.Exists(cache))
        {
            var locked = ReadDependencies(Path.Combine(packages, "packages-lock.json")).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in SafeDirectories(cache))
            {
                var folder = Path.GetFileName(dir);
                var at = folder.IndexOf('@');
                var id = ReadName(dir) ?? (at > 0 ? folder[..at] : folder);
                if (found.ContainsKey(id) || (!locked.Contains(id) && !manifest.ContainsKey(id))) continue;
                found[id] = new ProjectPackageRef(id, Path.GetFullPath(dir), PackageOrigin.Cache);
            }
        }

        var list = found.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var assets = Path.Combine(unityProjectPath, "Assets");
        if (includeAssets && Directory.Exists(assets)) list.Insert(0, new ProjectPackageRef(AssetsId, Path.GetFullPath(assets), PackageOrigin.Project));
        return list;
    }

    public static string? LocalFolder(string unityProjectPath, string? manifestValue)
    {
        if (manifestValue is null || !manifestValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        var target = manifestValue[5..].Trim();
        if (target.Length == 0 || target.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(target) ? target : Path.Combine(unityProjectPath, "Packages", target));
            return File.Exists(Path.Combine(full, "package.json")) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static ProjectPackageRef? Owner(IReadOnlyList<ProjectPackageRef> packages, string unityProjectPath, string? file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(unityProjectPath, file)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        ProjectPackageRef? best = null;
        foreach (var package in packages)
        {
            var root = Path.TrimEndingDirectorySeparator(package.Root) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, Platform.PathComparison()) && (best is null || root.Length > best.Root.Length + 1)) best = package;
        }
        return best;
    }

    public static string? ReadName(string folder)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } value ? value : Path.GetFileName(folder);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return Path.GetFileName(folder); }
    }

    public static Dictionary<string, string> ReadDependencies(string jsonPath)
    {
        var deps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(jsonPath)) return deps;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("dependencies", out var list) || list.ValueKind != JsonValueKind.Object) return deps;
            foreach (var dep in list.EnumerateObject())
                deps[dep.Name] = dep.Value.ValueKind switch
                {
                    JsonValueKind.String => dep.Value.GetString() ?? "",
                    JsonValueKind.Object when dep.Value.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String => version.GetString() ?? "",
                    _ => "",
                };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading the dependencies in {jsonPath}", ex); }
        return deps;
    }

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing {folder}", ex); return Array.Empty<string>(); }
    }
}
