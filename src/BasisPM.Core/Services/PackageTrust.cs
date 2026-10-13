using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class PackageTrust
{
    public static IReadOnlyList<string> ReviewedCommits(Catalog? catalog, string? packageId, CatalogPackageVersion? entry)
    {
        var versions = catalog is null || string.IsNullOrWhiteSpace(packageId)
            ? Enumerable.Empty<CatalogPackageVersion?>()
            : catalog.Packages.Where(p => string.Equals(p.Key, packageId.Trim(), StringComparison.OrdinalIgnoreCase)).SelectMany(p => p.Value.Versions.Values);
        return versions.Append(entry)
            .Select(v => v?.ReviewedCommit?.Trim())
            .Where(c => c is { Length: >= 7 } && c.All(Uri.IsHexDigit))
            .Select(c => c!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsTrusted(bool registry, IReadOnlyList<string> reviewedCommits, string? commit) =>
        registry && (reviewedCommits.Count == 0 || reviewedCommits.Any(c => PackageScanService.SameCommit(c, commit)));

    public static string? PinnedUrl(CatalogPackageVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.Url)) return null;
        if (version.ReviewedCommit?.Trim() is not { Length: >= 7 and <= 64 } commit || !commit.All(Uri.IsHexDigit) || UpmGitUrl.Parse(version.Url) is not { } parsed) return version.Url;
        return parsed.ToManifestUrl(commit, parsed.Path);
    }
}
