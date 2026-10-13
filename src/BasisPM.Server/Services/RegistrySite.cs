using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Server.Services;

public static class RegistrySite
{
    public const string ProjectFile = "BasisPM.Server.csproj";
    public const string SeedFolderEnvironment = "BASISPM_SEED_DIR";
    public const string ChecksFile = "checks.json";

    public static readonly JsonSerializerOptions OutputOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string? FindProjectFolder(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, ProjectFile))) return dir.FullName;
            var nested = Path.Combine(dir.FullName, "src", "BasisPM.Server");
            if (File.Exists(Path.Combine(nested, ProjectFile))) return nested;
            dir = dir.Parent;
        }
        return null;
    }

    public static string? SeedFolder(string? contentRoot = null)
    {
        if (Environment.GetEnvironmentVariable(SeedFolderEnvironment) is { Length: > 0 } custom) return Path.GetFullPath(custom);
        if (contentRoot is not null) return Path.Combine(contentRoot, "seed");
        var project = FindProjectFolder(AppContext.BaseDirectory) ?? FindProjectFolder(Directory.GetCurrentDirectory());
        if (project is not null) return Path.Combine(project, "seed");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "seed");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static string PackageListsFile(string seedFolder)
    {
        var current = Path.Combine(seedFolder, "packagelists.json");
        var legacy = Path.Combine(seedFolder, "bundles.json");
        return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
    }

    public static bool CopyChecks(string seedFolder, string outDir)
    {
        var source = Path.Combine(seedFolder, ChecksFile);
        if (!File.Exists(source)) return false;
        Directory.CreateDirectory(outDir);
        File.Copy(source, Path.Combine(outDir, ChecksFile), overwrite: true);
        return true;
    }
}
