using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public enum DependencyState { Present, UnityResolves, InCatalog, FromGit, Missing }

public sealed record PackageDependency(string Id, string Requested, PackageDependencyKind Kind, DependencyState State)
{
    public CatalogPackageVersion? CatalogEntry { get; init; }
    public string? GitUrl { get; init; }
    public bool CanInstall => State is DependencyState.InCatalog or DependencyState.FromGit;
}

public sealed record PackageDependent(string Id, string DisplayName, string Root);

public static class PackageDependencies
{
    public static IReadOnlySet<string> PresentIds(string unityProjectPath)
    {
        var packages = Path.Combine(unityProjectPath, "Packages");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith(UnityPackageMap.ReadDependencies(Path.Combine(packages, "manifest.json")).Keys);
        ids.UnionWith(UnityPackageMap.ReadDependencies(Path.Combine(packages, "packages-lock.json")).Keys);
        ids.UnionWith(UnityPackageMap.List(unityProjectPath, includeAssets: false).Select(p => p.Id));
        return ids;
    }

    public static IReadOnlyList<string> RegistryScopes(PackageManifest? manifest) =>
        manifest?.ScopedRegistries?.SelectMany(r => r.Scopes ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        ?? (IReadOnlyList<string>)Array.Empty<string>();

    public static IReadOnlyList<PackageDependency> Analyze(UpmPackageJson package, IReadOnlySet<string> present, IReadOnlyCollection<string> scopes, Catalog? catalog)
    {
        var declared = new Dictionary<string, (string Value, PackageDependencyKind Kind, string? GitUrl)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, value, kind) in PackageJsonFile.Dependencies(package))
        {
            if (string.IsNullOrWhiteSpace(id) || string.Equals(id, package.Name, StringComparison.OrdinalIgnoreCase)) continue;
            var git = IsGitUrl(value) ? value.Trim() : null;
            if (declared.TryGetValue(id, out var existing))
            {
                if (git is not null && existing.GitUrl is null) declared[id] = (existing.Value, existing.Kind, git);
                continue;
            }
            declared[id] = (value, kind, git);
        }

        var result = new List<PackageDependency>(declared.Count);
        foreach (var (id, (value, kind, git)) in declared)
        {
            if (present.Contains(id))
                result.Add(new PackageDependency(id, value, kind, DependencyState.Present));
            else if (Best(catalog, id, value) is { } entry)
                result.Add(new PackageDependency(id, value, kind, DependencyState.InCatalog) { CatalogEntry = entry });
            else if (git is not null)
                result.Add(new PackageDependency(id, value, kind, DependencyState.FromGit) { GitUrl = git });
            else if (kind == PackageDependencyKind.Unity && IsResolvedByUnity(id, scopes))
                result.Add(new PackageDependency(id, value, kind, DependencyState.UnityResolves));
            else
                result.Add(new PackageDependency(id, value, kind, DependencyState.Missing));
        }
        return result.OrderBy(d => d.State).ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<PackageDependency> AnalyzeInstalled(string unityProjectPath, string packageRoot, PackageManifest? manifest, Catalog? catalog) =>
        PackageJsonFile.Read(packageRoot) is { } package
            ? Analyze(package, PresentIds(unityProjectPath), RegistryScopes(manifest), catalog)
            : Array.Empty<PackageDependency>();

    public static IReadOnlyList<PackageDependent> FindDependents(string unityProjectPath, string packageId, Catalog? catalog = null)
    {
        var dependents = new Dictionary<string, PackageDependent>(StringComparer.OrdinalIgnoreCase);
        var installed = UnityPackageMap.List(unityProjectPath, includeAssets: false);
        foreach (var reference in installed)
        {
            if (string.Equals(reference.Id, packageId, StringComparison.OrdinalIgnoreCase)) continue;
            if (PackageJsonFile.Read(reference.Root) is not { } package) continue;
            if (PackageJsonFile.DependencyIds(package).Contains(packageId, StringComparer.OrdinalIgnoreCase))
                dependents.TryAdd(reference.Id, new PackageDependent(reference.Id, string.IsNullOrWhiteSpace(package.DisplayName) ? reference.Id : package.DisplayName, reference.Root));
        }
        if (catalog is not null)
        {
            foreach (var reference in installed)
            {
                if (dependents.ContainsKey(reference.Id) || string.Equals(reference.Id, packageId, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = Latest(catalog, reference.Id);
                if (entry?.Dependencies?.Keys.Contains(packageId, StringComparer.OrdinalIgnoreCase) == true)
                    dependents.TryAdd(reference.Id, new PackageDependent(reference.Id, string.IsNullOrWhiteSpace(entry.DisplayName) ? reference.Id : entry.DisplayName, reference.Root));
            }
        }
        return dependents.Values.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static bool IsResolvedByUnity(string id, IReadOnlyCollection<string> scopes) =>
        id.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)
        || scopes.Any(scope => id.Equals(scope, StringComparison.OrdinalIgnoreCase) || id.StartsWith(scope.TrimEnd('.') + ".", StringComparison.OrdinalIgnoreCase));

    private static bool IsGitUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && UpmGitUrl.Parse(value) is not null && GitUrlPolicy.IsSafeDependencyUrl(value.Trim());

    private static CatalogPackageVersion? Best(Catalog? catalog, string id, string requested)
    {
        if (catalog is null) return null;
        var package = catalog.Packages.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
        if (package?.Versions is not { Count: > 0 } versions) return null;
        var parsed = versions.Select(kv => (Ok: SemVer.TryParse(kv.Key, out var v), Version: v, Entry: kv.Value)).Where(t => t.Ok).OrderByDescending(t => t.Version).ToList();
        if (parsed.Count == 0) return versions.Values.First();
        if (SemVer.TryParse(requested, out var minimum))
            return parsed.FirstOrDefault(t => t.Version.CompareTo(minimum) >= 0).Entry ?? parsed[0].Entry;
        if (SemVerRange.TryParse(requested, out var range) && range is not null)
            return parsed.FirstOrDefault(t => range.Satisfies(t.Version)).Entry ?? parsed[0].Entry;
        return parsed[0].Entry;
    }

    private static CatalogPackageVersion? Latest(Catalog catalog, string id)
    {
        var package = catalog.Packages.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value;
        return package?.Versions?.Where(kv => SemVer.TryParse(kv.Key, out _)).OrderByDescending(kv => SemVer.Parse(kv.Key)).Select(kv => kv.Value).FirstOrDefault()
            ?? package?.Versions?.Values.FirstOrDefault();
    }
}
