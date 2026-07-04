using System.Text.Json;

namespace BasisPM.Core.Services;

/// <summary>A package the wizard has mounted locally for editing, remembered so we can restore it exactly.</summary>
public sealed record MountRecord(string InstallPath, string PackageId, string FolderPath, string OriginalManifestValue);

/// <summary>
/// Remembers packages mounted for editing (which project, which package, and the exact manifest line
/// that was replaced) in <c>%AppData%/BasisPM/mounts.json</c> — so "Swap back" restores the original.
/// </summary>
public sealed class MountRegistry
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();

    public MountRegistry(string? dataDir = null)
    {
        var dir = dataDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BasisPM");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "mounts.json");
    }

    private List<MountRecord> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<MountRecord>>(File.ReadAllText(_path)) ?? new();
        }
        catch { }
        return new();
    }

    private void Save(List<MountRecord> records)
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(records, Opts)); }
        catch { }
    }

    private static bool Same(MountRecord r, string installPath, string packageId) =>
        string.Equals(r.InstallPath, installPath, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(r.PackageId, packageId, StringComparison.OrdinalIgnoreCase);

    public MountRecord? Find(string installPath, string packageId)
    {
        lock (_gate) return Load().FirstOrDefault(r => Same(r, installPath, packageId));
    }

    public IReadOnlyList<MountRecord> ForInstall(string installPath)
    {
        lock (_gate)
            return Load().Where(r => string.Equals(r.InstallPath, installPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public void Add(MountRecord record)
    {
        lock (_gate)
        {
            var records = Load();
            records.RemoveAll(r => Same(r, record.InstallPath, record.PackageId));
            records.Add(record);
            Save(records);
        }
    }

    public void Remove(string installPath, string packageId)
    {
        lock (_gate)
        {
            var records = Load();
            if (records.RemoveAll(r => Same(r, installPath, packageId)) > 0) Save(records);
        }
    }
}
