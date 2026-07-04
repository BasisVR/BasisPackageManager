using System.Collections.Generic;
using System.Linq;

namespace BasisPM.Core.Models;

public enum VersionKind { Release, Tag, Branch }

/// <summary>One selectable version of a package: a git ref (null = default branch), a label, and how it was found.</summary>
public sealed record PackageVersionOption(string? Ref, string Label, bool IsPrerelease, VersionKind Kind);

/// <summary>Versions available for a package's repo, best-first, plus whether any published releases exist.</summary>
public sealed record PackageVersions(IReadOnlyList<PackageVersionOption> Options, bool HasReleases)
{
    public static readonly PackageVersions Empty = new(new List<PackageVersionOption>(), false);

    /// <summary>Newest stable release/tag (not a prerelease, not the default-branch fallback), or null.</summary>
    public PackageVersionOption? LatestStable =>
        Options.FirstOrDefault(o => !o.IsPrerelease && o.Kind != VersionKind.Branch);
}
