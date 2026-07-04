namespace BasisPM.Core.Services;

/// <summary>Free/total space on the volume that holds a path, plus a human-readable formatter.</summary>
public sealed record DiskSpaceInfo(long FreeBytes, long TotalBytes, string DriveName);

/// <summary>
/// Reports free disk space for a (possibly not-yet-created) path, so the app can warn before it
/// clones the large Basis repo onto a nearly-full drive. Returns null when the space can't be
/// determined (bad path, a network/UNC location, an unmounted drive) — callers treat that as "unknown".
/// </summary>
public static class DiskSpace
{
    public static DiskSpaceInfo? ForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path.Trim()));
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return null;
            return new DiskSpaceInfo(drive.AvailableFreeSpace, drive.TotalSize, drive.Name);
        }
        catch
        {
            return null; // UNC path, missing drive, permission issue, … — just don't warn.
        }
    }

    /// <summary>Formats a byte count as e.g. "7.3 GB" (binary units).</summary>
    public static string Human(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes < 0 ? 0 : bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {units[i]}";
    }
}
