using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
            if (FolderVersion(segments[i]) is { } version) return version;
        return null;
    }

    public static string? FolderVersion(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;
        var name = folderName.StartsWith("Unity-", StringComparison.OrdinalIgnoreCase) ? folderName[6..] : folderName;
        var dash = name.IndexOf('-');
        var version = dash < 0 ? name : name[..dash];
        return IsEditorVersion(version) ? version : null;
    }

    public static string? FolderArchitecture(string? folderName) =>
        folderName is null ? null : folderName.EndsWith("-arm64", StringComparison.OrdinalIgnoreCase) ? "arm64" : folderName.EndsWith("-x86_64", StringComparison.OrdinalIgnoreCase) ? "x86_64" : null;

    public static string NativeArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x86_64";

    public static string? ArchitectureFromHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[0] == 0xCF && header[1] == 0xFA && header[2] == 0xED && header[3] == 0xFE)
            return BitConverter.ToInt32(header[4..8]) switch { 0x01000007 => "x86_64", 0x0100000C => "arm64", _ => null };
        if (header.Length >= 20 && header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
            return BitConverter.ToUInt16(header[18..20]) switch { 0x3E => "x86_64", 0xB7 => "arm64", _ => null };
        if (header.Length < 0x40 || header[0] != (byte)'M' || header[1] != (byte)'Z') return null;
        var pe = BitConverter.ToInt32(header[0x3C..0x40]);
        if (pe <= 0 || header.Length < pe + 6 || header[pe] != (byte)'P' || header[pe + 1] != (byte)'E' || header[pe + 2] != 0 || header[pe + 3] != 0) return null;
        return BitConverter.ToUInt16(header[(pe + 4)..(pe + 6)]) switch { 0x8664 => "x86_64", 0xAA64 => "arm64", _ => null };
    }

    public static string? ExecutableArchitecture(string? editorPath)
    {
        if (editorPath is null) return null;
        if (RuntimeInformation.OSArchitecture != Architecture.Arm64) return "x86_64";
        try
        {
            var file = Directory.Exists(editorPath) ? Path.Combine(editorPath, "Contents", "MacOS", "Unity") : editorPath;
            if (!File.Exists(file)) return null;
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[4096];
            return ArchitectureFromHeader(buffer.AsSpan(0, stream.Read(buffer, 0, buffer.Length)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading the architecture of {editorPath}", ex); return null; }
    }

    public static IReadOnlyList<string> HubConfigFolders(OSPlatform os, string home, string appData, string? xdgConfigHome, string localAppData)
    {
        if (os == OSPlatform.Windows)
            return new[] { appData.Length > 0 ? Path.Combine(appData, "UnityHub") : "", localAppData.Length > 0 ? Path.Combine(localAppData, "Packages", "UnityTechnologies.UnityHub_2vrhnee42bhxm", "LocalCache", "Roaming", "UnityHub") : "" }.Where(f => f.Length > 0).ToList();
        if (home.Length == 0) return Array.Empty<string>();
        var user = home.TrimEnd('/');
        if (os == OSPlatform.OSX) return new[] { user + "/Library/Application Support/UnityHub" };
        var config = string.IsNullOrWhiteSpace(xdgConfigHome) ? user + "/.config" : xdgConfigHome.Trim().TrimEnd('/');
        return new[] { config + "/unityhub", config + "/UnityHub", user + "/.var/app/" + UnityHubService.FlatpakId + "/config/unityhub" };
    }

    public static IReadOnlyList<string> DefaultEditorRoots(OSPlatform os, string home, string programFiles)
    {
        if (os == OSPlatform.Windows) return programFiles.Length > 0 ? new[] { Path.Combine(programFiles, "Unity", "Hub", "Editor") } : Array.Empty<string>();
        if (os == OSPlatform.OSX) return new[] { "/Applications/Unity/Hub/Editor" };
        return home.Length > 0 ? new[] { home.TrimEnd('/') + "/Unity/Hub/Editor" } : Array.Empty<string>();
    }

    public static string? ParseSecondaryInstallPath(string json)
    {
        try { return JsonSerializer.Deserialize<string>(json) is { Length: > 0 } path ? path : null; }
        catch (JsonException) { return null; }
    }

    public static IReadOnlyList<string> ParseExcludedFolders(string json) => DataArray(json).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();

    public static IReadOnlyList<InstalledEditor> ParseLocatedEditors(string json) => DataArray(json)
        .Where(e => e.ValueKind == JsonValueKind.Object && IsEditorVersion(Text(e, "version")) && Location(e) is not null)
        .Select(e => new InstalledEditor { Version = Text(e, "version")!, Path = Location(e)!, Architecture = Text(e, "architecture") ?? "x86_64" })
        .ToList();

    public static string MachineConfigFolder(OSPlatform os, string? configPath, string commonAppData)
    {
        if (!string.IsNullOrWhiteSpace(configPath)) return configPath.Trim();
        if (os == OSPlatform.Windows) return Path.Combine(commonAppData, "Unity", "config");
        return os == OSPlatform.OSX ? "/Library/Application Support/Unity/config" : "/usr/share/unity3d/config";
    }

    public static string? ParseMachineWideInstallLocation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && Text(document.RootElement, "machineWideSecondaryInstallLocation") is { Length: > 0 } folder ? folder : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Location(JsonElement editor)
    {
        if (!editor.TryGetProperty("location", out var location)) return null;
        if (location.ValueKind == JsonValueKind.String) return location.GetString() is { Length: > 0 } single ? single : null;
        return location.ValueKind == JsonValueKind.Array && location.GetArrayLength() > 0 && location[0].ValueKind == JsonValueKind.String && location[0].GetString() is { Length: > 0 } first ? first : null;
    }

    public static IReadOnlyList<InstalledEditor> FindInstalled(string? version = null)
    {
        var os = Platform.Current;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configs = HubConfigFolders(os, home, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var machine = ReadJson(Path.Combine(MachineConfigFolder(os, Environment.GetEnvironmentVariable("CONFIG_PATH"), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)), "services-config.json"), ParseMachineWideInstallLocation);
        var roots = DefaultEditorRoots(os, home, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)).Concat(machine is null ? Array.Empty<string>() : new[] { machine });
        return FindInstalledIn(configs, roots, version, os);
    }

    public static IReadOnlyList<InstalledEditor> FindInstalledIn(IEnumerable<string> hubConfigFolders, IEnumerable<string> editorRoots, string? version, OSPlatform os)
    {
        var configs = hubConfigFolders.Where(Directory.Exists).ToList();
        var excluded = configs.SelectMany(c => ReadJson(Path.Combine(c, "excludedEditorFolders.json"), ParseExcludedFolders) ?? Array.Empty<string>()).ToList();
        var roots = editorRoots.Concat(configs.Select(c => ReadJson(Path.Combine(c, "secondaryInstallPath.json"), ParseSecondaryInstallPath)).OfType<string>());
        var found = new List<InstalledEditor>();
        foreach (var root in roots.Distinct(StringComparer.FromComparison(Platform.PathComparison(os))))
            foreach (var folder in Subfolders(root))
            {
                var name = Path.GetFileName(folder);
                if (FolderVersion(name) is not { } folderVersion || !Wanted(folderVersion) || excluded.Any(e => Platform.PathsEqual(e.TrimEnd('/', '\\'), folder))) continue;
                if (FindEditorExecutable(folder, os) is { } exe) Add(folderVersion, exe, FolderArchitecture(name), false);
            }
        foreach (var config in configs)
            foreach (var located in ReadJson(Path.Combine(config, "editors-v2.json"), ParseLocatedEditors) ?? Array.Empty<InstalledEditor>())
                if (Wanted(located.Version) && (File.Exists(located.Path) || Directory.Exists(located.Path))) Add(located.Version, located.Path, located.Architecture, true);
        return found.OrderByDescending(e => UnityVersion.TryParse(e.Version, out var v) ? v : null).ThenByDescending(e => e.Architecture == NativeArchitecture).ToList();

        bool Wanted(string candidate) => version is null || string.Equals(candidate, version.Trim(), StringComparison.OrdinalIgnoreCase);

        void Add(string editorVersion, string path, string? architecture, bool located)
        {
            if (found.Any(e => Platform.PathsEqual(e.Path, path))) return;
            found.Add(new InstalledEditor { Version = editorVersion, Path = path, Architecture = (located ? architecture : null) ?? ExecutableArchitecture(path) ?? architecture });
        }
    }

    private static IEnumerable<JsonElement> DataArray(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var data = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var inner) ? inner : root;
            return data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().Select(e => e.Clone()).ToList() : Array.Empty<JsonElement>();
        }
        catch (JsonException) { return Array.Empty<JsonElement>(); }
    }

    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static T? ReadJson<T>(string file, Func<string, T?> parse) where T : class
    {
        try { return File.Exists(file) ? parse(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {file}", ex); return null; }
    }

    private static IEnumerable<string> Subfolders(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing Unity editors in {root}", ex); return Array.Empty<string>(); }
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
        catch (Exception ex) { DiagnosticLog.Write($"Reading Unity version from macOS editor at {appBundlePath}", ex); return null; }
    }

    private static string? VersionFromWindowsExe(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            return VersionFromProductVersion(FileVersionInfo.GetVersionInfo(exePath).ProductVersion);
        }
        catch (Exception ex) { DiagnosticLog.Write($"Reading Unity version from executable {exePath}", ex); return null; }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+[abfpx]\d+(c\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex EditorVersionRegex();

    [GeneratedRegex(@"<key>\s*CFBundleVersion\s*</key>\s*<string>\s*([^<\s]+)\s*</string>", RegexOptions.IgnoreCase)]
    private static partial Regex CfBundleVersionRegex();
}
