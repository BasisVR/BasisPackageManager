using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BasisPM.Core.Services;

/// <summary>
/// Cross-platform helpers (Windows / macOS / Linux). The OS-specific decisions are kept as
/// <b>pure functions parameterised by <see cref="OSPlatform"/></b> so every branch is unit-testable
/// on any host — the impure wrappers just pass <see cref="Current"/> and touch the real OS.
/// </summary>
public static class Platform
{
    /// <summary>The running OS, normalised to one of Windows / OSX / Linux.</summary>
    public static OSPlatform Current { get; } =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows :
        RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX :
        OSPlatform.Linux;

    /// <summary>
    /// How to compare filesystem paths on a given OS. Linux ext-family filesystems are
    /// case-<i>sensitive</i>; Windows (NTFS) and the default macOS (APFS) are case-<i>insensitive</i>.
    /// </summary>
    public static StringComparison PathComparison(OSPlatform os) =>
        os == OSPlatform.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Path comparison for the running OS.</summary>
    public static StringComparison PathComparison() => PathComparison(Current);

    /// <summary>Case-correct path equality for the running OS.</summary>
    public static bool PathsEqual(string? a, string? b) => string.Equals(a, b, PathComparison());
}

/// <summary>A resolved "how to start this process" description — the pure output of the launcher decisions.</summary>
public readonly record struct LaunchSpec(string FileName, IReadOnlyList<string> Arguments, bool UseShellExecute)
{
    public ProcessStartInfo ToStartInfo()
    {
        var psi = new ProcessStartInfo { FileName = FileName, UseShellExecute = UseShellExecute };
        foreach (var a in Arguments) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>
/// Launches native GUI applications cross-platform. Launching a raw executable via
/// <c>UseShellExecute=true</c> is the classic macOS trap: the runtime routes it through <c>open</c>,
/// which expects a document or <c>.app</c> bundle, not the inner Mach-O binary — so the Unity Hub /
/// editor path (which points at that inner binary) silently fails to open. The specs below avoid it.
/// (Opening plain folders/URLs is <i>not</i> here — for those, .NET's own <c>UseShellExecute=true</c>
/// already maps to ShellExecute/<c>open</c>/<c>xdg-open</c> correctly, and the app uses that.)
/// </summary>
public static class AppLauncher
{
    /// <summary>
    /// Launch a native GUI application by a path, optionally with arguments. On macOS a path that lives
    /// inside (or is) a <c>.app</c> bundle is launched via <c>open</c> against the bundle, since the inner
    /// Mach-O binary can't be shell-opened; on Windows/Linux the executable is run directly.
    /// </summary>
    public static LaunchSpec GuiAppSpec(OSPlatform os, string appPath, IReadOnlyList<string>? args = null)
    {
        args ??= Array.Empty<string>();
        if (os == OSPlatform.OSX)
        {
            var bundle = AppBundlePath(appPath);
            if (bundle is not null)
            {
                var openArgs = new List<string> { bundle };
                if (args.Count > 0)
                {
                    openArgs.Add("--args");
                    openArgs.AddRange(args);
                }
                return new LaunchSpec("open", openArgs, UseShellExecute: false);
            }
        }
        return new LaunchSpec(appPath, args, UseShellExecute: false);
    }

    /// <summary>
    /// The <c>.app</c> bundle a path belongs to, or null if none. Pure string logic:
    /// <c>/Applications/Unity Hub.app/Contents/MacOS/Unity Hub</c> → <c>/Applications/Unity Hub.app</c>;
    /// a path that already ends in <c>.app</c> maps to itself.
    /// </summary>
    public static string? AppBundlePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        const string marker = ".app";
        foreach (var sep in new[] { '/', '\\' })
        {
            var at = path.IndexOf(marker + sep, StringComparison.OrdinalIgnoreCase);
            if (at >= 0) return path[..(at + marker.Length)];
        }
        return path.EndsWith(marker, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    // ---- impure wrapper (uses the running OS) ----

    /// <summary>Launch a native GUI app by path (+ optional args). Never throws.</summary>
    public static bool LaunchGuiApp(string appPath, IReadOnlyList<string>? args = null)
    {
        try { Process.Start(GuiAppSpec(Platform.Current, appPath, args).ToStartInfo()); return true; }
        catch { return false; }
    }
}

/// <summary>
/// Locates an executable on PATH, cross-platform. The search itself is a pure function so it can be
/// tested with a fake PATH + predicate; <see cref="Locate"/> wires in the real filesystem, trying the
/// Windows executable extensions and requiring a POSIX execute bit on Unix (so a non-executable file
/// named <c>git</c> earlier on PATH isn't mistaken for the tool).
/// </summary>
public static class ExecutableFinder
{
    /// <summary>Executable name suffixes to try for a bare tool name on the given OS (Windows adds PATHEXT-style suffixes).</summary>
    public static IReadOnlyList<string> Extensions(OSPlatform os) =>
        os == OSPlatform.Windows ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };

    /// <summary>
    /// Pure PATH search: returns the first <c>dir/name+ext</c> for which <paramref name="isCandidate"/>
    /// is true, scanning <paramref name="pathEnv"/> in order. No filesystem access of its own.
    /// </summary>
    public static string? SelectFromPath(string exeName, string? pathEnv, IReadOnlyList<string> extensions, Func<string, bool> isCandidate)
    {
        if (string.IsNullOrEmpty(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                string candidate;
                try { candidate = Path.Combine(dir, exeName + ext); }
                catch { continue; } // invalid chars in a PATH entry
                if (isCandidate(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Locate a tool (bare name, no extension) on the real PATH for the running OS, or null.</summary>
    public static string? Locate(string exeName) =>
        SelectFromPath(exeName, Environment.GetEnvironmentVariable("PATH"), Extensions(Platform.Current), IsRunnableFile);

    /// <summary>True if the path is an existing file that is executable on this OS (POSIX exec bit on Unix).</summary>
    public static bool IsRunnableFile(string path)
    {
        if (!File.Exists(path)) return false;
        if (Platform.Current == OSPlatform.Windows) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return true; } // if the mode can't be read, don't be stricter than File.Exists
    }
}
