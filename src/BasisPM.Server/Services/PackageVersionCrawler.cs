using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Server.Models;

namespace BasisPM.Server.Services;

public sealed class PackageVersionCrawler
{
    public const int DefaultMaxVersions = 20;

    private readonly IPackageVersionSource _source;
    private readonly int _maxVersions;
    private readonly int _parallelism;

    public PackageVersionCrawler(IPackageVersionSource source, int maxVersions = DefaultMaxVersions, int parallelism = 4)
    {
        _source = source;
        _maxVersions = Math.Max(1, maxVersions);
        _parallelism = Math.Max(1, parallelism);
    }

    public async Task<List<RegistryVersion>> CrawlAsync(RegistryPackage package, CancellationToken ct = default)
    {
        if (UpmGitUrl.Parse(package.GitUrl) is not { Ref: null } repo) return new List<RegistryVersion>();
        var tags = await _source.ListTagsAsync(repo, ct).ConfigureAwait(false);
        if (tags is null) return new List<RegistryVersion>();

        var picked = new List<(RepoTag Tag, SemVer Version)>();
        foreach (var tag in tags)
            if (GitUrlPolicy.IsSafeRef(tag.Name) && SemVer.TryParseTag(tag.Name, out var version)) picked.Add((tag, version));
        picked = picked
            .GroupBy(p => p.Version.ToString())
            .Select(g => g.First())
            .OrderByDescending(p => p.Version)
            .Take(_maxVersions)
            .ToList();

        var results = new RegistryVersion?[picked.Count];
        using var gate = new SemaphoreSlim(_parallelism);
        await Task.WhenAll(picked.Select(async (item, index) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var lookup = await _source.ReadPackageJsonAsync(repo, item.Tag.Name, isTag: true, ct).ConfigureAwait(false);
                if (lookup.Package is { } manifest && string.Equals(manifest.Name, package.Id, StringComparison.OrdinalIgnoreCase))
                    results[index] = FromManifest(manifest, item.Version.ToString(), item.Tag.Name, item.Tag.Commit);
            }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return results.OfType<RegistryVersion>().ToList();
    }

    public async Task<RegistryVersion?> ReadCurrentAsync(RegistryPackage package, CancellationToken ct = default)
    {
        if (UpmGitUrl.Parse(package.GitUrl) is not { } repo) return null;
        var lookup = await _source.ReadPackageJsonAsync(repo, repo.Ref, isTag: false, ct).ConfigureAwait(false);
        if (lookup.Package is not { } manifest) return null;
        if (!string.IsNullOrWhiteSpace(manifest.Name) && !string.Equals(manifest.Name, package.Id, StringComparison.OrdinalIgnoreCase)) return null;
        var version = SemVer.TryParse(manifest.Version, out var parsed) ? parsed.ToString() : package.Version;
        return FromManifest(manifest, version, repo.Ref, null);
    }

    public static RegistryVersion FromManifest(UpmPackageJson manifest, string version, string? gitRef, string? commit) => new()
    {
        Version = version,
        Ref = string.IsNullOrWhiteSpace(gitRef) ? null : gitRef,
        Commit = string.IsNullOrWhiteSpace(commit) ? null : commit,
        DisplayName = Text(manifest.DisplayName),
        Description = Text(manifest.Description),
        Unity = Text(manifest.Unity),
        UnityRelease = Text(manifest.UnityRelease),
        Basis = manifest.Basis,
        License = Text(manifest.License),
        Dependencies = Dependencies(manifest),
        GitDependencies = GitDependencies(manifest),
    };

    public static Dictionary<string, string>? Dependencies(UpmPackageJson manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in new[] { manifest.Dependencies, manifest.VpmDependencies })
            foreach (var (id, range) in source ?? new Dictionary<string, string>())
            {
                var value = range?.Trim();
                if (string.IsNullOrEmpty(value) || !PackageStore.IsSafeId(id) || id.Length > 214 || result.Keys.Any(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))) continue;
                if (SemVerRange.TryParse(value, out _) || GitUrlPolicy.IsHostedGitUrl(value)) result[id] = value;
            }
        return result.Count == 0 ? null : result;
    }

    public static Dictionary<string, string>? GitDependencies(UpmPackageJson manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, url) in manifest.GitDependencies ?? new Dictionary<string, string>())
        {
            var value = url?.Trim();
            if (!string.IsNullOrEmpty(value) && PackageStore.IsSafeId(id) && GitUrlPolicy.IsHostedGitUrl(value) && GitUrlPolicy.IsSafeDependencyUrl(value)) result[id] = value;
        }
        return result.Count == 0 ? null : result;
    }

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
