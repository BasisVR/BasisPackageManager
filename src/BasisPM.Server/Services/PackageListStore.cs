using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Server.Services;

/// <summary>
/// Loads curated package lists from seed/packagelists.json (committed, PR-editable) — the package-list
/// equivalent of <see cref="PackageStore"/>. Package lists are submitted from the app as GitHub issues
/// and merged into the seed, so there's no runtime write path.
/// </summary>
public sealed class PackageListStore
{
    private static readonly JsonSerializerOptions FileOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<PackageList> _packageLists;

    public PackageListStore(string? seedPath) => _packageLists = LoadSeed(seedPath);

    public static List<PackageList> LoadSeed(string? seedPath)
    {
        if (!string.IsNullOrEmpty(seedPath) && File.Exists(seedPath))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<PackageList>>(File.ReadAllText(seedPath), FileOpts);
                if (list is not null) return list;
            }
            catch { }
        }
        return new List<PackageList>();
    }

    public IReadOnlyList<PackageList> All() => _packageLists.ToList();

    public PackageList? Get(string id) =>
        _packageLists.FirstOrDefault(pl => string.Equals(pl.Id, id, StringComparison.OrdinalIgnoreCase));
}
