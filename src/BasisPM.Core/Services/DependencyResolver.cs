using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class DependencyResolver
{
    private readonly CatalogService _catalogService;

    public DependencyResolver(CatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public ResolutionResult Resolve(Catalog catalog, IEnumerable<(string Name, string Range)> requested)
    {
        var resolved = new Dictionary<string, CatalogPackageVersion>(StringComparer.Ordinal);
        var missing = new List<string>();
        var conflicts = new List<string>();

        var queue = new Queue<(string Name, string Range, string Origin)>();
        foreach (var r in requested) queue.Enqueue((r.Name, r.Range, "(requested)"));

        while (queue.Count > 0)
        {
            var (name, rangeSpec, origin) = queue.Dequeue();
            SemVerRange range;
            try { range = SemVerRange.Parse(rangeSpec); }
            catch { conflicts.Add($"{name}: invalid range \"{rangeSpec}\" from {origin}"); continue; }

            var best = _catalogService.FindBest(catalog, name, range);
            if (best is null)
            {
                missing.Add($"{name} {rangeSpec} (from {origin})");
                continue;
            }

            if (resolved.TryGetValue(name, out var existing))
            {
                var existingVer = SemVer.Parse(existing.Version);
                var bestVer = SemVer.Parse(best.Version);
                if (bestVer.CompareTo(existingVer) > 0)
                {
                    if (!range.Satisfies(existingVer))
                        resolved[name] = best;
                }
                else if (!range.Satisfies(existingVer))
                {
                    conflicts.Add($"{name}: {origin} requires {rangeSpec}, already pinned to {existing.Version}");
                }
                continue;
            }

            resolved[name] = best;
            if (best.Dependencies is null) continue;
            foreach (var (depName, depRange) in best.Dependencies)
                queue.Enqueue((depName, depRange, $"{name}@{best.Version}"));
        }

        return new ResolutionResult(resolved, missing, conflicts);
    }
}

public sealed record ResolutionResult(
    IReadOnlyDictionary<string, CatalogPackageVersion> Resolved,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Conflicts)
{
    public bool Ok => Missing.Count == 0 && Conflicts.Count == 0;
}
