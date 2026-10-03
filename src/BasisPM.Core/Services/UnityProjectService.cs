using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class UnityProjectService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsUnityProject(string path) => IsUnityRoot(path);

    public DetectionResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DetectionResult.Fail("No path provided.");
        if (!Directory.Exists(path))
            return DetectionResult.Fail("Folder does not exist.");

        if (IsUnityRoot(path))
            return DetectionResult.Ok(path);

        var cursor = new DirectoryInfo(path).Parent;
        while (cursor is not null)
        {
            if (IsUnityRoot(cursor.FullName))
                return DetectionResult.Ok(cursor.FullName, $"Resolved upward to {cursor.FullName}.");
            cursor = cursor.Parent;
        }

        try
        {
            foreach (var sub in Directory.EnumerateDirectories(path))
            {
                if (IsUnityRoot(sub))
                    return DetectionResult.Ok(sub, $"Resolved into subfolder {Path.GetFileName(sub)}.");
            }
        }
        catch { }

        return DetectionResult.Fail(IdentifyReason(path));
    }

    private static bool IsUnityRoot(string path)
    {
        if (!Directory.Exists(path)) return false;
        if (!Directory.Exists(Path.Combine(path, "Assets"))) return false;
        if (!Directory.Exists(Path.Combine(path, "ProjectSettings"))) return false;
        if (!File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"))) return false;
        return true;
    }

    private static string IdentifyReason(string path)
    {
        if (!Directory.Exists(Path.Combine(path, "Assets")))
            return "Not a Unity project: missing Assets folder.";
        if (!Directory.Exists(Path.Combine(path, "ProjectSettings")))
            return "Not a Unity project: missing ProjectSettings folder.";
        if (!File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt")))
            return "Not a Unity project: missing ProjectSettings/ProjectVersion.txt.";
        return "Not a Unity project.";
    }

    public async Task<UnityProjectInfo> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!IsUnityProject(path))
            throw new InvalidOperationException($"Not a Unity project: {path}");

        var version = await ReadProjectVersionAsync(path, ct).ConfigureAwait(false);
        var manifest = await ReadManifestAsync(path, ct).ConfigureAwait(false);

        return new UnityProjectInfo
        {
            Path = path,
            Name = new DirectoryInfo(path).Name,
            UnityVersion = version,
            Manifest = manifest,
        };
    }

    public Task SaveManifestAsync(UnityProjectInfo project, CancellationToken ct = default) =>
        SaveManifestAsync(project.Path, project.Manifest, ct);

    public async Task SaveManifestAsync(string unityProjectPath, PackageManifest manifest, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(unityProjectPath, "Packages", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await using var fs = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(fs, manifest, JsonOpts, ct).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Enumerates the embedded packages (each <c>Packages/&lt;folder&gt;/package.json</c>) of a Unity project.</summary>
    public IReadOnlyList<LocalPackage> ListEmbeddedPackages(string unityProjectPath)
    {
        var result = new List<LocalPackage>();
        var packagesDir = Path.Combine(unityProjectPath, "Packages");
        if (!Directory.Exists(packagesDir)) return result;

        foreach (var dir in Directory.EnumerateDirectories(packagesDir))
        {
            var packageJson = Path.Combine(dir, "package.json");
            if (!File.Exists(packageJson)) continue;

            UpmPackageJson? meta = null;
            try { meta = JsonSerializer.Deserialize<UpmPackageJson>(File.ReadAllText(packageJson), ReadOpts); }
            catch { }

            var folder = Path.GetFileName(dir);
            var id = string.IsNullOrWhiteSpace(meta?.Name) ? folder : meta!.Name;
            result.Add(new LocalPackage(
                Id: id,
                DisplayName: string.IsNullOrWhiteSpace(meta?.DisplayName) ? id : meta!.DisplayName,
                Version: meta?.Version ?? "",
                Description: meta?.Description ?? "",
                FolderName: folder,
                FolderPath: dir,
                PackageJsonPath: packageJson,
                IsGitRepo: Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))));
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private static async Task<string> ReadProjectVersionAsync(string path, CancellationToken ct)
    {
        var versionPath = Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionPath)) return "unknown";
        var lines = await File.ReadAllLinesAsync(versionPath, ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                return line["m_EditorVersion:".Length..].Trim();
        }
        return "unknown";
    }

    private static async Task<PackageManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        var manifestPath = Path.Combine(path, "Packages", "manifest.json");
        if (!File.Exists(manifestPath)) return new PackageManifest();
        await using var fs = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<PackageManifest>(fs, JsonOpts, ct).ConfigureAwait(false)
               ?? new PackageManifest();
    }
}
