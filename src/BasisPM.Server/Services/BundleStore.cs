using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Server.Services;

/// <summary>
/// Loads curated bundles from seed/bundles.json (committed, PR-editable) — the bundle equivalent
/// of <see cref="PackageStore"/>. Bundles are submitted from the app as GitHub issues and merged
/// into the seed, so there's no runtime write path.
/// </summary>
public sealed class BundleStore
{
    private static readonly JsonSerializerOptions FileOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly List<Bundle> _bundles;

    public BundleStore(string? seedPath) => _bundles = LoadSeed(seedPath);

    public static List<Bundle> LoadSeed(string? seedPath)
    {
        if (!string.IsNullOrEmpty(seedPath) && File.Exists(seedPath))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<Bundle>>(File.ReadAllText(seedPath), FileOpts);
                if (list is not null) return list;
            }
            catch { }
        }
        return new List<Bundle>();
    }

    public IReadOnlyList<Bundle> All() => _bundles.ToList();

    public Bundle? Get(string id) =>
        _bundles.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
}
