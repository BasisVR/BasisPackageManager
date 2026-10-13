namespace BasisPM.Core.Models;

public enum StorageArea { Library, PackageCache, Temp, Logs, Obj, DevClone, ServerBuild, Other }

public sealed record StorageEntry(StorageArea Area, string Path, long Bytes, long Files, string? Name = null);

public sealed record StorageProgress(long Files, long Bytes);

public sealed record ProjectStorageReport(string RepoRoot, string UnityProjectPath, long TotalBytes, long TotalFiles, IReadOnlyList<StorageEntry> Entries)
{
    public StorageEntry? Find(StorageArea area) => Entries.FirstOrDefault(e => e.Area == area);
    public long BytesOf(StorageArea area) => Entries.Where(e => e.Area == area).Sum(e => e.Bytes);
    public IReadOnlyList<StorageEntry> Clones => Entries.Where(e => e.Area == StorageArea.DevClone).OrderByDescending(e => e.Bytes).ToList();
}

public enum UnusedCloneReason { Listed, LocalWork }

public sealed record UnusedClone(string Id, string Folder, bool Removable, UnusedCloneReason? Reason);

public sealed record StorageCleanResult(bool Ok, string Message, long Freed, IReadOnlyList<string> Removed, IReadOnlyList<string> Skipped)
{
    public static StorageCleanResult Fail(string message) => new(false, message, 0, Array.Empty<string>(), Array.Empty<string>());
}
