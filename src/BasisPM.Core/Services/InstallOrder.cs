using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class InstallOrder
{
    // Reorders a batch of pending installs so anything another item depends on (directly, or through
    // packages that aren't in the batch) installs before it. Unrelated items keep their order, and a
    // cycle still installs each member once.
    public static List<T> DependenciesFirst<T>(IReadOnlyList<T> items, Func<T, string> keyOf, Func<string, IEnumerable<string>> dependencyKeysOf)
    {
        var pending = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var key = keyOf(item);
            if (!pending.TryGetValue(key, out var list))
            {
                pending[key] = list = new List<T>();
            }
            list.Add(item);
        }
        var ordered = new List<T>(items.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string key)
        {
            if (!visited.Add(key))
            {
                return;
            }
            foreach (var dependency in dependencyKeysOf(key))
            {
                Visit(dependency);
            }
            if (pending.Remove(key, out var found))
            {
                ordered.AddRange(found);
            }
        }
        foreach (var item in items)
        {
            Visit(keyOf(item));
        }
        return ordered;
    }

    public static IEnumerable<string> DependencyIds(CatalogPackageVersion? entry) =>
        entry is null ? Enumerable.Empty<string>() : (entry.Dependencies?.Keys ?? Enumerable.Empty<string>()).Concat(entry.GitDependencies?.Keys ?? Enumerable.Empty<string>());
}
