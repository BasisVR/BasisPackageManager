using System.Reflection;
using System.Runtime.InteropServices;

namespace BasisPM.Core.Services;

public static class CommandLineTool
{
    public const string Name = "basispm";

    public static bool IsCliInvocation(string? argv0, string? appImage) => !string.IsNullOrEmpty(appImage) && !string.IsNullOrEmpty(argv0) && Path.GetFileName(argv0) == Name;

    public static string? Target(OSPlatform os, string? appImage, string baseDirectory, Func<string, bool> fileExists)
    {
        if (os == OSPlatform.Linux) return string.IsNullOrEmpty(appImage) || !fileExists(appImage) ? null : appImage;
        if (os != OSPlatform.OSX) return null;
        var cli = Path.Combine(baseDirectory, Name);
        return baseDirectory.Contains(".app/Contents/MacOS", StringComparison.Ordinal) && fileExists(cli) ? cli : null;
    }

    public static IReadOnlyList<string> LinkFolders(OSPlatform os, string home)
    {
        var local = home.TrimEnd('/') + "/.local/bin";
        return os == OSPlatform.OSX ? new[] { "/usr/local/bin", local } : new[] { local };
    }

    public static bool IsOnPath(string? path, string folder) =>
        (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Any(p => string.Equals(p.TrimEnd('/'), folder.TrimEnd('/'), StringComparison.Ordinal));

    public static string? CurrentTarget => Target(Platform.Current, Environment.GetEnvironmentVariable("APPIMAGE"), AppContext.BaseDirectory, File.Exists);

    public static string? InstalledLink()
    {
        if (CurrentTarget is not { } target) return null;
        foreach (var folder in LinkFolders(Platform.Current, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            var link = Path.Combine(folder, Name);
            try
            {
                if (new FileInfo(link).LinkTarget is { } pointsAt && Platform.PathsEqual(pointsAt, target)) return link;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {link}", ex); }
        }
        return null;
    }

    public static (string Link, bool? OnPath) Install()
    {
        var target = CurrentTarget ?? throw new InvalidOperationException("The command-line tool is only part of the installed app.");
        var (link, onPath) = InstallLink(target, LinkFolders(Platform.Current, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), ToolSearchPath.OriginalPath);
        return (link, onPath || Platform.Current != OSPlatform.OSX ? onPath : null);
    }

    public static (string Link, bool OnPath) InstallLink(string target, IEnumerable<string> folders, string? path)
    {
        Exception? last = null;
        foreach (var folder in folders)
        {
            if (folder == "/usr/local/bin" && !CanWrite(folder)) continue;
            try
            {
                Directory.CreateDirectory(folder);
                var link = Path.Combine(folder, Name);
                var existing = new FileInfo(link);
                if (existing.LinkTarget is not null) File.Delete(link);
                else if (existing.Exists) throw new IOException($"{link} already exists and isn't a link to this app.");
                File.CreateSymbolicLink(link, target);
                return (link, folder == "/usr/local/bin" || IsOnPath(path, folder));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { last = ex; }
        }
        throw new IOException(last?.Message ?? "No folder for the link could be written.", last);
    }

    public static int? RunBundled(string[] args)
    {
        var cli = Path.Combine(AppContext.BaseDirectory, Name + ".dll");
        if (!File.Exists(cli)) return null;
        var entry = Assembly.LoadFrom(cli).EntryPoint;
        if (entry is null) return null;
        var result = entry.Invoke(null, entry.GetParameters().Length == 0 ? null : new object[] { args });
        return result is int code ? code : 0;
    }

    private static bool CanWrite(string folder)
    {
        if (!Directory.Exists(folder) || OperatingSystem.IsWindows()) return false;
        try
        {
            var probe = Path.Combine(folder, ".basispm-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}
