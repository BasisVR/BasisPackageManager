using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class BpmStore
{
    public const string ManifestFileName = "bpm-manifest.json";
    public const string LockFileName = "bpm-lock.json";
    public const string LocalFileName = "bpm-local.json";
    public const string LegacyFileName = "basispackages.json";

    internal static readonly object Gate = new();

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ManifestPath(string projectPath) => Path.Combine(projectPath, ManifestFileName);
    public static string LockPath(string projectPath) => Path.Combine(projectPath, LockFileName);
    public static string LegacyPath(string projectPath) => Path.Combine(projectPath, "Packages", LegacyFileName);
    public static string LocalPath(string basisDevRoot) => Path.Combine(basisDevRoot, LocalFileName);

    public static bool Exists(string projectPath) => File.Exists(ManifestPath(projectPath)) || File.Exists(LegacyPath(projectPath));

    public static BpmManifest ReadManifest(string projectPath)
    {
        var path = ManifestPath(projectPath);
        if (File.Exists(path)) return Normalize(Load<BpmManifest>(path, "a package manifest"));
        return LoadLegacy(projectPath) is { } legacy ? FromLegacy(legacy).Manifest : new BpmManifest();
    }

    public static BpmLock ReadLock(string projectPath)
    {
        var path = LockPath(projectPath);
        if (File.Exists(path)) return Normalize(Load<BpmLock>(path, "a package lock"));
        return !File.Exists(ManifestPath(projectPath)) && LoadLegacy(projectPath) is { } legacy ? FromLegacy(legacy).Lock : new BpmLock();
    }

    public static void Update(string projectPath, Action<BpmManifest, BpmLock> change)
    {
        lock (Gate)
        {
            var manifest = ReadManifest(projectPath);
            var locked = ReadLock(projectPath);
            change(manifest, locked);
            Save(projectPath, manifest, locked);
        }
    }

    public static void Save(string projectPath, BpmManifest manifest, BpmLock locked)
    {
        lock (Gate)
        {
            manifest.Format = BpmManifest.CurrentFormat;
            locked.Format = BpmLock.CurrentFormat;
            manifest.Packages = Sorted(manifest.Packages);
            if (manifest.Checkouts is { } checkouts) manifest.Checkouts = checkouts.Count == 0 ? null : Sorted(checkouts);
            locked.Packages = Sorted(locked.Packages);
            if (locked.Checkouts is { } lockedCheckouts) locked.Checkouts = lockedCheckouts.Count == 0 ? null : Sorted(lockedCheckouts);
            Write(ManifestPath(projectPath), manifest.IsEmpty ? null : manifest);
            Write(LockPath(projectPath), locked.IsEmpty ? null : locked);
            var legacy = LegacyPath(projectPath);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
    }

    public static BpmLocalState ReadLocal(string basisDevRoot)
    {
        var path = LocalPath(basisDevRoot);
        if (!File.Exists(path)) return new BpmLocalState();
        try { return Load<BpmLocalState>(path, "local settings") is { } state ? Fix(state) : new BpmLocalState(); }
        catch (InvalidDataException ex)
        {
            DiagnosticLog.Write($"Reading {path}", ex);
            AtomicFile.KeepUnreadableCopy(path);
            return new BpmLocalState();
        }

        static BpmLocalState Fix(BpmLocalState state)
        {
            state.Projects ??= new Dictionary<string, BpmLocalProject>();
            return state;
        }
    }

    public static BpmLocalProject LocalProject(BpmLocalState state, string basisDevRoot, string projectPath)
    {
        var key = LocalKey(basisDevRoot, projectPath);
        if (!state.Projects.TryGetValue(key, out var project) || project is null) state.Projects[key] = project = new BpmLocalProject();
        return project;
    }

    public static void UpdateLocal(string basisDevRoot, string projectPath, Action<BpmLocalProject> change)
    {
        lock (Gate)
        {
            var state = ReadLocal(basisDevRoot);
            var project = LocalProject(state, basisDevRoot, projectPath);
            change(project);
            if (project.Overrides is { Count: 0 }) project.Overrides = null;
            if (project.Mounted is { Count: 0 }) project.Mounted = null;
            if (project.Overrides is null && project.Mounted is null && project.Extra is not { Count: > 0 }) state.Projects.Remove(LocalKey(basisDevRoot, projectPath));
            state.Format = BpmLocalState.CurrentFormat;
            state.Projects = Sorted(state.Projects);
            Directory.CreateDirectory(basisDevRoot);
            Write(LocalPath(basisDevRoot), state.Projects.Count == 0 && state.Extra is not { Count: > 0 } ? null : state);
        }
    }

    public static string LocalKey(string basisDevRoot, string projectPath)
    {
        var owner = Path.GetDirectoryName(Path.GetFullPath(basisDevRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? basisDevRoot;
        var relative = Path.GetRelativePath(owner, Path.GetFullPath(projectPath)).Replace('\\', '/').TrimEnd('/');
        return relative.Length == 0 ? "." : relative;
    }

    internal static (BpmManifest Manifest, BpmLock Lock) FromLegacy(BasisPackagesFile legacy)
    {
        var manifest = new BpmManifest { Extra = legacy.Extra };
        var locked = new BpmLock();
        foreach (var (id, entry) in legacy.Packages)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Url)) continue;
            var url = entry.Url.Trim();
            var requested = BpmVersion.Git(url, entry.Path, entry.Ref);
            manifest.Packages[id] = new BpmManifestPackage { Version = requested, ForkOf = Blank(entry.ForkOf), Extra = entry.Extra };
            locked.Packages[id] = new BpmLockPackage { Version = BpmVersion.Git(url, entry.Path, Blank(entry.Commit) ?? entry.Ref), Requested = requested, PackageVersion = Blank(entry.Version), ForkOf = Blank(entry.ForkOf) };
        }
        return (manifest, locked);
    }

    private static BasisPackagesFile? LoadLegacy(string projectPath)
    {
        var path = LegacyPath(projectPath);
        if (!File.Exists(path)) return null;
        var file = Load<BasisPackagesFile>(path, "a package list");
        file.Packages ??= new Dictionary<string, BasisPackageEntry>();
        return file;
    }

    private static T Load<T>(string path, string what) where T : class
    {
        T? value;
        try { value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (JsonException ex) { throw new InvalidDataException($"{path} isn't valid JSON ({ex.Message}). Fix it, for example a merge conflict, then try again.", ex); }
        return value ?? throw new InvalidDataException($"{path} doesn't hold {what}. Fix or delete it, then try again.");
    }

    private static BpmManifest Normalize(BpmManifest manifest)
    {
        manifest.Packages ??= new Dictionary<string, BpmManifestPackage>();
        foreach (var key in manifest.Packages.Where(p => p.Value is null).Select(p => p.Key).ToList()) manifest.Packages.Remove(key);
        if (manifest.Checkouts is { } checkouts)
            foreach (var key in checkouts.Where(p => p.Value is null).Select(p => p.Key).ToList()) checkouts.Remove(key);
        return manifest;
    }

    private static BpmLock Normalize(BpmLock locked)
    {
        locked.Packages ??= new Dictionary<string, BpmLockPackage>();
        foreach (var key in locked.Packages.Where(p => p.Value is null).Select(p => p.Key).ToList()) locked.Packages.Remove(key);
        if (locked.Checkouts is { } checkouts)
            foreach (var key in checkouts.Where(p => p.Value is null).Select(p => p.Key).ToList()) checkouts.Remove(key);
        return locked;
    }

    private static Dictionary<string, T> Sorted<T>(Dictionary<string, T> map) =>
        map.OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private static void Write<T>(string path, T? value) where T : class
    {
        if (value is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var text = JsonSerializer.Serialize(value, Json) + "\n";
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal)) return;
        AtomicFile.WriteAllText(path, text);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
