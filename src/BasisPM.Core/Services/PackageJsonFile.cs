using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class PackageJsonFile
{
    public const string FileName = "package.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PathIn(string packageRoot) => Path.Combine(packageRoot, FileName);

    public static UpmPackageJson? Read(string packageRootOrFile)
    {
        var path = packageRootOrFile.EndsWith(FileName, StringComparison.OrdinalIgnoreCase) && File.Exists(packageRootOrFile) ? packageRootOrFile : PathIn(packageRootOrFile);
        try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading {path}", ex);
            return null;
        }
    }

    public static UpmPackageJson? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var package = JsonSerializer.Deserialize<UpmPackageJson>(text, Options);
            if (package is null) return null;
            package.Name ??= "";
            package.DisplayName ??= "";
            package.Version ??= "";
            package.Description ??= "";
            return package;
        }
        catch (JsonException) { return null; }
    }

    public static IEnumerable<(string Id, string Value, PackageDependencyKind Kind)> Dependencies(UpmPackageJson package)
    {
        foreach (var (id, value) in package.Dependencies ?? new Dictionary<string, string>()) yield return (id, value, PackageDependencyKind.Unity);
        foreach (var (id, value) in package.VpmDependencies ?? new Dictionary<string, string>()) yield return (id, value, PackageDependencyKind.Vpm);
        foreach (var (id, value) in package.GitDependencies ?? new Dictionary<string, string>()) yield return (id, value, PackageDependencyKind.Git);
    }

    public static IReadOnlyCollection<string> DependencyIds(UpmPackageJson package) =>
        Dependencies(package).Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

public enum PackageDependencyKind { Unity, Vpm, Git }
