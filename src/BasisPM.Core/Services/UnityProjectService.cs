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
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public bool IsUnityProject(string path)
    {
        return IsUnityRoot(path);
    }

    public DetectionResult Detect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DetectionResult.Fail("No path provided.");
        }

        if (!Directory.Exists(path))
        {
            return DetectionResult.Fail("Folder does not exist.");
        }

        if (IsUnityRoot(path))
        {
            return DetectionResult.Ok(path);
        }

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
        catch (Exception ex) { DiagnosticLog.Write($"Detecting Unity project at {path}", ex); }

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
        return !Directory.Exists(Path.Combine(path, "Assets"))
            ? "Not a Unity project: missing Assets folder."
            : !Directory.Exists(Path.Combine(path, "ProjectSettings"))
            ? "Not a Unity project: missing ProjectSettings folder."
            : !File.Exists(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"))
            ? "Not a Unity project: missing ProjectSettings/ProjectVersion.txt."
            : "Not a Unity project.";
    }

    public async Task<UnityProjectInfo> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!IsUnityProject(path))
        {
            throw new InvalidOperationException($"Not a Unity project: {path}");
        }

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

    public static Task SaveManifestAsync(UnityProjectInfo project, CancellationToken ct = default) => SaveManifestAsync(project.Path, project.Manifest, ct);

    public static async Task SaveManifestAsync(string unityProjectPath, PackageManifest manifest, CancellationToken ct = default)
    {
        var manifestPath = Path.Combine(unityProjectPath, "Packages", "manifest.json");
        if (File.Exists(manifestPath))
        {
            try
            {
                await using var existing = File.OpenRead(manifestPath);
                await JsonSerializer.DeserializeAsync<PackageManifest>(existing, JsonOpts, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{manifestPath} isn't valid JSON, so it was left unchanged. Fix or restore it, then try again.", ex);
            }
        }
        await AtomicFile.WriteAsync(manifestPath, stream => JsonSerializer.SerializeAsync(stream, manifest, JsonOpts, ct), ct).ConfigureAwait(false);
    }
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    /// <summary>Enumerates the embedded packages (each <c>Packages/&lt;folder&gt;/package.json</c>) of a Unity project.</summary>
    public IReadOnlyList<LocalPackage> ListEmbeddedPackages(string unityProjectPath)
    {
        var result = new List<LocalPackage>();
        var packagesDir = Path.Combine(unityProjectPath, "Packages");
        if (!Directory.Exists(packagesDir))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(packagesDir))
        {
            var packageJson = Path.Combine(dir, "package.json");
            if (!File.Exists(packageJson)) continue;

            UpmPackageJson? meta = null;
            try { meta = JsonSerializer.Deserialize<UpmPackageJson>(File.ReadAllText(packageJson), ReadOpts); }
            catch (Exception ex) { DiagnosticLog.Write($"Reading embedded package metadata from {packageJson}", ex); }

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

    public static bool IsOpenInUnity(string unityProjectPath)
    {
        var lockFile = Path.Combine(unityProjectPath, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static string ReadUnityVersion(string unityProjectPath)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(unityProjectPath, "ProjectSettings", "ProjectVersion.txt")))
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal)) return line["m_EditorVersion:".Length..].Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading the Unity version of {unityProjectPath}", ex); }
        return "unknown";
    }

    private static async Task<string> ReadProjectVersionAsync(string path, CancellationToken ct)
    {
        var versionPath = Path.Combine(path, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionPath))
        {
            return "unknown";
        }

        var lines = await File.ReadAllLinesAsync(versionPath, ct).ConfigureAwait(false);
        for (int LineIndex = 0; LineIndex < lines.Length; LineIndex++)
        {
            string? line = lines[LineIndex];
            if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
            {
                return line["m_EditorVersion:".Length..].Trim();
            }
        }
        return "unknown";
    }

    private static async Task<PackageManifest> ReadManifestAsync(string path, CancellationToken ct)
    {
        var manifestPath = Path.Combine(path, "Packages", "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new PackageManifest();
        }

        await using var fs = File.OpenRead(manifestPath);
        return await JsonSerializer.DeserializeAsync<PackageManifest>(fs, JsonOpts, ct).ConfigureAwait(false) ?? new PackageManifest();
    }
}
