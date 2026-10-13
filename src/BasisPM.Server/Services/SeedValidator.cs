using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Server.Models;

namespace BasisPM.Server.Services;

public sealed record SeedIssue(string File, string? Package, bool Error, string Message);

public sealed class SeedCheckResult
{
    public List<(RegistryPackage Package, string File)> Packages { get; } = new();
    public List<SeedIssue> Issues { get; } = new();
    public int Errors => Issues.Count(i => i.Error);
    public int Warnings => Issues.Count(i => !i.Error);
}

public static class SeedValidator
{
    private const int MaxIdLength = 214;
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9_-]*(\.[a-z0-9_-]+)+$", RegexOptions.CultureInvariant);
    private static readonly Regex UnityPattern = new(@"^\d+\.\d+$", RegexOptions.CultureInvariant);
    private static readonly string[] GeneratedFields = { "stars", "forks", "updated", "versions", "source" };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static SeedCheckResult Load(string seedFolder)
    {
        var result = new SeedCheckResult();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in PackageStore.SeedFiles(seedFolder))
        {
            var file = $"seed/{PackageStore.SeedPackagesFolder}/{Path.GetFileName(path)}";
            RegistryPackage? package;
            HashSet<string> fields;
            try
            {
                var text = File.ReadAllText(path);
                using var document = JsonDocument.Parse(text);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    result.Issues.Add(new SeedIssue(file, null, true, "has to hold one package object."));
                    continue;
                }
                fields = document.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                package = JsonSerializer.Deserialize<RegistryPackage>(text, ReadOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                result.Issues.Add(new SeedIssue(file, null, true, $"isn't valid JSON: {ex.Message}"));
                continue;
            }
            if (package is null) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(package.Id))
            {
                result.Issues.Add(new SeedIssue(file, null, true, "has no id."));
                continue;
            }
            if (!string.Equals(package.Id, name, StringComparison.Ordinal))
                result.Issues.Add(new SeedIssue(file, package.Id, true, $"holds {package.Id}, so it has to be named {package.Id}.json."));
            foreach (var field in GeneratedFields.Where(fields.Contains))
                result.Issues.Add(new SeedIssue(file, package.Id, false, $"\"{field}\" is filled in when the registry is built; remove it from the seed."));
            if (seen.TryGetValue(package.Id, out var other))
            {
                result.Issues.Add(new SeedIssue(file, package.Id, true, $"{package.Id} is already listed in {other}."));
                continue;
            }
            seen[package.Id] = file;
            result.Packages.Add((package, file));
        }

        var legacy = Path.Combine(seedFolder, PackageStore.LegacySeedFile);
        if (File.Exists(legacy))
        {
            List<RegistryPackage>? listed = null;
            try { listed = JsonSerializer.Deserialize<List<RegistryPackage>>(File.ReadAllText(legacy), ReadOptions); }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                result.Issues.Add(new SeedIssue("seed/" + PackageStore.LegacySeedFile, null, true, $"isn't valid JSON: {ex.Message}"));
            }
            if (listed is { Count: > 0 })
            {
                result.Issues.Add(new SeedIssue("seed/" + PackageStore.LegacySeedFile, null, false, $"still lists {listed.Count} package(s); move each one into seed/{PackageStore.SeedPackagesFolder}/<id>.json."));
                foreach (var package in listed.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id)))
                {
                    if (seen.TryGetValue(package.Id, out var other))
                        result.Issues.Add(new SeedIssue("seed/" + PackageStore.LegacySeedFile, package.Id, false, $"{package.Id} is also in {other}, which wins."));
                    else
                    {
                        seen[package.Id] = "seed/" + PackageStore.LegacySeedFile;
                        result.Packages.Add((package, "seed/" + PackageStore.LegacySeedFile));
                    }
                }
            }
        }

        var lists = Path.Combine(seedFolder, "packagelists.json");
        if (File.Exists(lists))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(lists));
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                    result.Issues.Add(new SeedIssue("seed/packagelists.json", null, true, "has to hold a JSON array of package lists."));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                result.Issues.Add(new SeedIssue("seed/packagelists.json", null, true, $"isn't valid JSON: {ex.Message}"));
            }
        }
        return result;
    }

    public static IEnumerable<SeedIssue> CheckEntry(RegistryPackage p, string file, string? iconsFolder)
    {
        SeedIssue Error(string message) => new(file, p.Id, true, message);
        SeedIssue Warning(string message) => new(file, p.Id, false, message);

        if (string.IsNullOrWhiteSpace(p.Name)) yield return Error("name is required.");
        if (string.IsNullOrWhiteSpace(p.Description)) yield return Error("description is required.");
        if (string.IsNullOrWhiteSpace(p.Author)) yield return Error("author is required.");
        if (p.Id.Length > MaxIdLength) yield return Error($"id is longer than {MaxIdLength} characters.");
        else if (!IdPattern.IsMatch(p.Id)) yield return Error("id has to be a lowercase package name like com.company.package (letters, digits, '-', '_' and '.').");
        else if (p.Id.Split('.').Length < 3) yield return Warning("ids usually have three parts, like com.company.package.");

        if (string.IsNullOrWhiteSpace(p.GitUrl)) yield return Error("gitUrl is required.");
        else if (!GitUrlPolicy.IsHostedGitUrl(p.GitUrl)) yield return Error("gitUrl has to be an https URL on github.com or gitlab.com.");
        else if (!GitUrlPolicy.IsSafeDependencyUrl(p.GitUrl)) yield return Error("gitUrl has an invalid ?path= sub-folder or #ref.");

        foreach (var (field, value) in new[] { ("repoUrl", p.RepoUrl), ("authorUrl", p.AuthorUrl), ("discord", p.Discord), ("donate", p.Donate), ("link", p.Link) })
            if (!string.IsNullOrWhiteSpace(value) && !GitUrlPolicy.IsWebUrl(value)) yield return Error($"{field} has to be an http(s) URL.");

        if (!string.IsNullOrWhiteSpace(p.Version) && !SemVer.TryParse(p.Version, out _)) yield return Error("version has to be a semantic version such as 1.2.0.");
        if (!string.IsNullOrWhiteSpace(p.Unity) && !UnityPattern.IsMatch(p.Unity.Trim())) yield return Warning("unity should be a Unity version like 6000.0.");

        foreach (var (id, range) in p.Dependencies ?? new Dictionary<string, string>())
            if (!PackageStore.IsSafeId(id) || string.IsNullOrWhiteSpace(range) || !(SemVerRange.TryParse(range, out _) || GitUrlPolicy.IsHostedGitUrl(range)))
                yield return Error($"dependency \"{id}\" needs a package id and a version range or a hosted git URL.");

        if (!string.IsNullOrWhiteSpace(p.Image))
        {
            var image = p.Image.Trim();
            if (image.StartsWith("icons/", StringComparison.OrdinalIgnoreCase))
            {
                var name = image["icons/".Length..];
                if (name.Length == 0 || name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal)
                    || iconsFolder is null || !File.Exists(Path.Combine(iconsFolder, name)))
                    yield return Error($"image points at {image}, but there is no such file in src/BasisPM.Server/wwwroot/icons.");
            }
            else if (!PackageStore.IsOwnRepoImage(p))
                yield return Error("image has to be icons/<file> in this repository or a raw image URL in the package's own repository.");
        }
    }

    public static async Task<List<SeedIssue>> CheckRemoteAsync(RegistryPackage p, string file, IPackageVersionSource source, CancellationToken ct = default)
    {
        var issues = new List<SeedIssue>();
        if (UpmGitUrl.Parse(p.GitUrl) is not { } repo || !GitUrlPolicy.IsHostedGitUrl(p.GitUrl)) return issues;
        var exists = await source.RepoExistsAsync(repo, ct).ConfigureAwait(false);
        if (exists == false)
        {
            issues.Add(new SeedIssue(file, p.Id, true, $"the repository {repo.Slug} on {repo.Host} wasn't found."));
            return issues;
        }
        if (exists is null)
        {
            issues.Add(new SeedIssue(file, p.Id, false, $"couldn't reach {repo.Host} to check {repo.Slug}."));
            return issues;
        }
        var where = (string.IsNullOrEmpty(repo.Path) ? "the root" : repo.Path) + (repo.Ref is null ? " of the default branch" : $" at {repo.Ref}");
        var lookup = await source.ReadPackageJsonAsync(repo, repo.Ref, isTag: false, ct).ConfigureAwait(false);
        if (!lookup.Reachable)
            issues.Add(new SeedIssue(file, p.Id, false, $"couldn't read package.json from {where}."));
        else if (lookup.Package is not { } manifest)
            issues.Add(new SeedIssue(file, p.Id, true, $"there is no package.json in {where}."));
        else
        {
            if (!string.Equals(manifest.Name, p.Id, StringComparison.Ordinal))
                issues.Add(new SeedIssue(file, p.Id, true, $"package.json in {where} is named \"{manifest.Name}\", not \"{p.Id}\"."));
            if (string.IsNullOrWhiteSpace(p.License) && string.IsNullOrWhiteSpace(manifest.License))
                issues.Add(new SeedIssue(file, p.Id, true, "has no license: set \"license\" in package.json or in the seed file."));
        }
        return issues;
    }

    public static IEnumerable<SeedIssue> CheckLicenseOffline(RegistryPackage p, string file)
    {
        if (string.IsNullOrWhiteSpace(p.License))
            yield return new SeedIssue(file, p.Id, false, "has no license in the seed; the online check reads it from package.json.");
    }

    public static async Task<SeedCheckResult> CheckAsync(string seedFolder, string? iconsFolder, IPackageVersionSource? source, CancellationToken ct = default)
    {
        var result = Load(seedFolder);
        foreach (var (package, file) in result.Packages)
        {
            result.Issues.AddRange(CheckEntry(package, file, iconsFolder));
            if (source is null) result.Issues.AddRange(CheckLicenseOffline(package, file));
            else result.Issues.AddRange(await CheckRemoteAsync(package, file, source, ct).ConfigureAwait(false));
        }
        return result;
    }

    public static string Format(SeedCheckResult result)
    {
        var text = new StringBuilder();
        foreach (var issue in result.Issues.OrderByDescending(i => i.Error).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase))
            text.Append(issue.Error ? "error    " : "warning  ").Append(issue.File).Append(": ").AppendLine(issue.Message);
        text.Append($"Checked {result.Packages.Count} package(s): {result.Errors} error(s), {result.Warnings} warning(s).");
        return text.ToString();
    }
}
