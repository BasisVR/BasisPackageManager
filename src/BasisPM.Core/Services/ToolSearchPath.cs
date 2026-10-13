using System.Runtime.InteropServices;

namespace BasisPM.Core.Services;

public static class ToolSearchPath
{
    private static readonly string[] MacPackageManagerFolders = { "/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin", "/opt/local/bin", "/opt/local/sbin" };
    private static readonly string[] SystemFolders = { "/usr/bin", "/bin" };

    private static string? _originalPath;

    public static string? OriginalPath => _originalPath ?? Environment.GetEnvironmentVariable("PATH");

    public static IReadOnlyList<string> Leading(OSPlatform os) => os == OSPlatform.OSX ? MacPackageManagerFolders : Array.Empty<string>();

    public static IReadOnlyList<string> Trailing(OSPlatform os, string home, string? dotnetRoot, IEnumerable<string> systemPaths)
    {
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(dotnetRoot)) folders.Add(dotnetRoot.Trim());
        if (os == OSPlatform.OSX)
        {
            folders.AddRange(systemPaths);
            folders.Add("/usr/local/share/dotnet");
        }
        if (string.IsNullOrEmpty(home)) return folders;
        home = home.TrimEnd('/');
        folders.AddRange(new[] { home + "/.dotnet", home + "/.dotnet/tools", home + "/.local/bin" });
        if (os != OSPlatform.Linux) return folders;
        folders.AddRange(new[] { home + "/bin", "/usr/local/bin", "/usr/games", "/snap/bin", "/var/lib/flatpak/exports/bin", home + "/.local/share/flatpak/exports/bin" });
        folders.AddRange(new[] { "/home/linuxbrew/.linuxbrew/bin", home + "/.linuxbrew/bin", home + "/.nix-profile/bin", "/nix/var/nix/profiles/default/bin", "/run/current-system/sw/bin" });
        return folders;
    }

    public static string? Augment(string? path, OSPlatform os, string home, string? dotnetRoot, IEnumerable<string> systemPaths, Func<string, bool> directoryExists)
    {
        if (os == OSPlatform.Windows) return null;
        var entries = (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        var known = new HashSet<string>(entries.Select(Normalize), StringComparer.Ordinal);
        var changed = false;
        var insertAt = entries.FindIndex(e => SystemFolders.Contains(Normalize(e)));
        foreach (var folder in Leading(os))
        {
            if (!Add(folder)) continue;
            if (insertAt < 0) entries.Add(folder);
            else entries.Insert(insertAt++, folder);
        }
        foreach (var folder in Trailing(os, home, dotnetRoot, systemPaths))
            if (Add(folder)) entries.Add(folder);
        return changed ? string.Join(':', entries) : null;

        bool Add(string folder)
        {
            if (folder.Length == 0 || folder[0] != '/' || folder.Contains(':') || !known.Add(Normalize(folder)) || !directoryExists(folder)) return false;
            changed = true;
            return true;
        }
    }

    public static IReadOnlyList<string> ReadSystemPaths(string root = "/etc")
    {
        var lines = new List<string>();
        try
        {
            var files = new List<string> { Path.Combine(root, "paths") };
            var folder = Path.Combine(root, "paths.d");
            if (Directory.Exists(folder)) files.AddRange(Directory.GetFiles(folder).Order(StringComparer.Ordinal));
            foreach (var file in files.Where(File.Exists))
                lines.AddRange(File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Reading the system search path", ex); }
        return lines;
    }

    public static void Apply()
    {
        try
        {
            var os = Platform.Current;
            _originalPath ??= Environment.GetEnvironmentVariable("PATH");
            if (os == OSPlatform.Windows) return;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var systemPaths = os == OSPlatform.OSX ? ReadSystemPaths() : Array.Empty<string>();
            var path = Augment(Environment.GetEnvironmentVariable("PATH"), os, home, Environment.GetEnvironmentVariable("DOTNET_ROOT"), systemPaths, Directory.Exists);
            if (path is not null) Environment.SetEnvironmentVariable("PATH", path);
        }
        catch (Exception ex) { DiagnosticLog.Write("Adding tool folders to the search path", ex); }
    }

    private static string Normalize(string folder) => folder.Length > 1 ? folder.TrimEnd('/') : folder;
}
