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
            var full = Path.GetFullPath(path.Trim());
            DriveInfo? drive;
            if (OperatingSystem.IsWindows())
            {
                var root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return null;
                drive = new DriveInfo(root);
            }
            else
            {
                // On Unix every path shares the "/" root, so Path.GetPathRoot would always report the
                // root filesystem. Pick instead the mounted volume whose mount point is the longest
                // prefix of the target — that's the drive that actually holds it (e.g. a separate /home).
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToList();
                var mount = SelectMountRoot(drives.Select(d => d.Name).ToList(), full);
                drive = mount is null ? null : drives.FirstOrDefault(d => d.Name == mount);
            }
            if (drive is null || !drive.IsReady) return null;
            return new DiskSpaceInfo(drive.AvailableFreeSpace, drive.TotalSize, drive.Name);
        }
        catch
        {
            return null; // UNC path, missing drive, permission issue, … — just don't warn.
        }
    }

    /// <summary>
    /// Of a set of mounted volume roots, the one that best contains <paramref name="fullPath"/>: the
    /// longest root that is a whole-segment prefix of it (so "/home" wins over "/" for "/home/u/x", and
    /// "/home" does not match "/home2/x"). Pure — unit-testable without real drives. Null if none match.
    /// </summary>
    public static string? SelectMountRoot(IReadOnlyList<string> mountPoints, string fullPath)
    {
        string? best = null;
        foreach (var m in mountPoints)
        {
            if (string.IsNullOrEmpty(m) || !PathIsUnder(fullPath, m)) continue;
            if (best is null || m.Length > best.Length) best = m;
        }
        return best;
    }

    // True if `path` is inside (or equal to) mount root `root`, matched on whole path segments.
    private static bool PathIsUnder(string path, string root)
    {
        var r = root.Length > 1 ? root.TrimEnd('/') : root;      // "/home/" -> "/home"; keep "/" as "/"
        if (r == "/") return path.StartsWith('/');               // root fs contains every absolute path
        if (!path.StartsWith(r, StringComparison.Ordinal)) return false;
        return path.Length == r.Length || path[r.Length] == '/'; // segment boundary
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
