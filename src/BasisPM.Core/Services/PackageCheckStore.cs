using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class PackageCheckStore
{
    public const string FolderName = "checks";
    public const int MaxIssues = 500;
    public const int MaxDiagnostics = 500;

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    public PackageCheckStore(string? root = null)
    {
        Root = root ?? Path.Combine(AppDataPaths.Root, FolderName);
    }

    public string Root { get; }

    public string PathFor(string unityProjectPath) => Path.Combine(Root, KeyFor(unityProjectPath) + ".json");

    public static string KeyFor(string unityProjectPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(unityProjectPath)).Replace('\\', '/');
        if (Platform.PathComparison() == StringComparison.OrdinalIgnoreCase) full = full.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
    }

    public ProjectCheckHistory? Load(string unityProjectPath)
    {
        var path = PathFor(unityProjectPath);
        lock (_gate)
        {
            if (!File.Exists(path)) return null;
            try { return JsonSerializer.Deserialize<ProjectCheckHistory>(File.ReadAllText(path), Json); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                DiagnosticLog.Write($"Reading the package checks for {unityProjectPath}", ex);
                AtomicFile.KeepUnreadableCopy(path);
                return null;
            }
        }
    }

    public PackageCheckState? Find(string unityProjectPath, string packageId) => Load(unityProjectPath)?.Packages.GetValueOrDefault(packageId);

    public ProjectCheckHistory Save(ProjectCheckReport report)
    {
        lock (_gate)
        {
            var history = Load(report.Project) ?? new ProjectCheckHistory { Project = report.Project };
            history.Project = report.Project;
            history.Last = Trim(report);
            foreach (var (id, state) in report.Packages) history.Packages[id] = state;
            Write(history);
            return history;
        }
    }

    public void Forget(string unityProjectPath, string packageId)
    {
        lock (_gate)
        {
            if (Load(unityProjectPath) is not { } history || !history.Packages.Remove(packageId)) return;
            Write(history);
        }
    }

    private void Write(ProjectCheckHistory history)
    {
        try { AtomicFile.WriteAllText(PathFor(history.Project), JsonSerializer.Serialize(history, Json) + "\n"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Saving the package checks for {history.Project}", ex); }
    }

    private static ProjectCheckReport Trim(ProjectCheckReport report)
    {
        if (report.Issues.Count > MaxIssues) report.Issues = report.Issues.OrderByDescending(i => i.Severity).Take(MaxIssues).ToList();
        if (report.Compile is { Diagnostics.Count: > MaxDiagnostics } compile) compile.Diagnostics.RemoveRange(MaxDiagnostics, compile.Diagnostics.Count - MaxDiagnostics);
        return report;
    }
}
