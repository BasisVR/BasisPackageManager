using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>
/// Resolves a Unity editor (its launch path + exact version) from a folder the user points at, so
/// people <b>without Unity Hub</b> can register an editor manually. Following the same shape as
/// <see cref="Platform"/>, the OS-specific decisions are <b>pure functions parameterised by
/// <see cref="OSPlatform"/></b> (candidate paths, version string recognition, Info.plist parsing) so
/// every branch is unit-testable on any host; the impure <see cref="TryResolve"/> wraps them and
/// touches the real filesystem.
/// </summary>
public sealed partial class UnityEditorLocator
{
    /// <summary>
    /// Try to resolve the folder the user picked into a launchable editor. Accepts the version-named
    /// root (contains <c>Editor/</c>), the <c>Editor</c> folder itself, or a macOS <c>.app</c> bundle.
    /// Returns false (no throw) when no editor executable is found or its version can't be determined.
    /// </summary>
    public bool TryResolve(string? folder, out InstalledEditor editor)
    {
        editor = null!;
        if (string.IsNullOrWhiteSpace(folder)) return false;
        folder = folder.Trim();
        // Tolerate the user picking the executable itself rather than a folder.
        if (File.Exists(folder)) folder = Path.GetDirectoryName(folder) ?? folder;
        if (!Directory.Exists(folder)) return false;

        var exe = FindEditorExecutable(folder, Platform.Current);
        if (exe is null) return false;

        var version = DetectVersion(exe, Platform.Current);
        if (version is null) return false;

        editor = new InstalledEditor { Version = version, Path = exe, IsManual = true };
        return true;
    }

    /// <summary>The launchable editor path under <paramref name="folder"/> for the given OS, or null.</summary>
    public static string? FindEditorExecutable(string folder, OSPlatform os)
    {
        foreach (var candidate in EditorExecutableCandidates(folder, os))
        {
            // On macOS the editor is a .app bundle (a directory); elsewhere it's a file.
            if (os == OSPlatform.OSX ? Directory.Exists(candidate) : File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// Ordered candidate paths for the editor binary given the folder the user picked. Pure string
    /// logic (no filesystem access) so it's testable for every OS on any host.
    /// </summary>
    public static IEnumerable<string> EditorExecutableCandidates(string folder, OSPlatform os)
    {
        if (os == OSPlatform.OSX)
        {
            // The user may pick the version root (contains Unity.app) or the bundle itself.
            if (folder.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                yield return folder;
            yield return Path.Combine(folder, "Unity.app");
            yield return Path.Combine(folder, "Editor", "Unity.app");
            yield break;
        }

        var exeName = os == OSPlatform.Windows ? "Unity.exe" : "Unity";
        // Hub layout: <version>/Editor/Unity[.exe]. Also accept the user pointing straight at Editor/.
        yield return Path.Combine(folder, "Editor", exeName);
        yield return Path.Combine(folder, exeName);
    }

    /// <summary>
    /// Whether a raw string is a real Unity <i>editor</i> version — the strict installer/Hub folder
    /// form <c>MAJOR.MINOR.PATCH{channel}{build}</c> (e.g. <c>6000.0.30f1</c>). Deliberately stricter
    /// than <see cref="UnityVersion.TryParse"/>: it requires an explicit channel+build so a generic
    /// semver folder like <c>1.2.3</c> on the path can't be mistaken for a Unity version.
    /// </summary>
    public static bool IsEditorVersion(string? s) => !string.IsNullOrEmpty(s) && EditorVersionRegex().IsMatch(s);

    /// <summary>
    /// The first path segment (searching nearest-to-executable first) that looks like a Unity editor
    /// version, or null. This is the most reliable source: Unity Hub and the official installers both
    /// name the editor folder exactly the version, on every OS.
    /// </summary>
    public static string? VersionFromPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 1; i >= 0; i--)
            if (IsEditorVersion(segments[i])) return segments[i];
        return null;
    }

    /// <summary>
    /// Extracts <c>CFBundleVersion</c> from a macOS <c>Info.plist</c>'s XML text if it's a valid Unity
    /// editor version, else null. Pure (text in, no file access) so it's testable without a Mac.
    /// </summary>
    public static string? VersionFromInfoPlistText(string? plistXml)
    {
        if (string.IsNullOrEmpty(plistXml)) return null;
        var m = CfBundleVersionRegex().Match(plistXml);
        return m.Success && IsEditorVersion(m.Groups[1].Value.Trim()) ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>
    /// The Unity editor version carried by a Windows <c>Unity.exe</c>'s ProductVersion, or null. Unity
    /// appends the changeset after an underscore (real example: <c>6000.5.2f1_eb73d3b415a1</c>), so the
    /// version is the part before it. Pure (string in) so it's testable without the real executable.
    /// </summary>
    public static string? VersionFromProductVersion(string? productVersion)
    {
        if (string.IsNullOrWhiteSpace(productVersion)) return null;
        var version = productVersion.Trim().Split('_', 2)[0];
        return IsEditorVersion(version) ? version : null;
    }

    // ---- impure version detection (uses the filesystem / PE resources) ----

    private static string? DetectVersion(string exePath, OSPlatform os)
    {
        // 1) The version-named path segment — exact and cross-platform (Hub/installer layout).
        if (VersionFromPath(exePath) is { } fromPath) return fromPath;

        // 2) macOS: read the app bundle's Info.plist.
        if (os == OSPlatform.OSX && VersionFromInfoPlistFile(exePath) is { } fromPlist) return fromPlist;

        // 3) Windows: the executable's embedded product version.
        if (os == OSPlatform.Windows && VersionFromWindowsExe(exePath) is { } fromExe) return fromExe;

        return null;
    }

    private static string? VersionFromInfoPlistFile(string appBundlePath)
    {
        try
        {
            var plist = Path.Combine(appBundlePath, "Contents", "Info.plist");
            return File.Exists(plist) ? VersionFromInfoPlistText(File.ReadAllText(plist)) : null;
        }
        catch { return null; }
    }

    private static string? VersionFromWindowsExe(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            return VersionFromProductVersion(FileVersionInfo.GetVersionInfo(exePath).ProductVersion);
        }
        catch { return null; }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+[abfpx]\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex EditorVersionRegex();

    [GeneratedRegex(@"<key>\s*CFBundleVersion\s*</key>\s*<string>\s*([^<\s]+)\s*</string>", RegexOptions.IgnoreCase)]
    private static partial Regex CfBundleVersionRegex();
}
