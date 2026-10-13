using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed partial class PackageAnalyzer
{
    public const int MaxFilesPerIssue = 10;
    private const int MaxFilesPerPackage = 80000;
    private const long MaxSourceBytes = 4L * 1024 * 1024;
    private const long MaxAssemblyBytes = 64L * 1024 * 1024;
    private const string RuntimePredefined = "Assembly-CSharp", EditorPredefined = "Assembly-CSharp-Editor";
    private static readonly string[] PredefinedNames = { RuntimePredefined, EditorPredefined, "Assembly-CSharp-firstpass", "Assembly-CSharp-Editor-firstpass" };
    private static readonly string[] AlwaysKnownRoots = { "System", "Microsoft", "Mono" };
    private static readonly string[] FallbackRoots = { "System", "Microsoft", "Mono", "UnityEngine", "UnityEditor", "UnityEngineInternal", "UnityEditorInternal", "Unity", "AOT", "TMPro", "NUnit", "JetBrains" };
    private static readonly string[] UnityRoots = { "UnityEngine", "UnityEditor", "UnityEngineInternal", "UnityEditorInternal", "Unity", "TMPro", "NUnit", "JetBrains" };
    private static readonly string[] ForeignRoots = { "UnityEngine", "UnityEditor", "Unity", "System", "Microsoft", "TMPro" };
    private static readonly HashSet<string> JunkNames = new(StringComparer.OrdinalIgnoreCase) { "Library", "Temp", "obj", "bin", "Logs", "UserSettings" };
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> EngineNamespaceCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> CoreTypeNames = new(StringComparer.Ordinal)
    {
        "Object", "Debug", "Random", "Input", "Time", "Application", "Physics", "Mathf", "Vector2", "Vector3", "Vector4", "Quaternion", "Color", "Color32", "Rect", "Bounds",
        "Ray", "Transform", "GameObject", "Component", "MonoBehaviour", "ScriptableObject", "Camera", "Light", "Material", "Shader", "Texture", "Texture2D", "Mesh", "Renderer",
        "Resources", "Screen", "Gizmos", "Matrix4x4", "Coroutine", "Action", "Func", "Task", "Math", "String", "Exception", "Type", "Array", "List", "Dictionary", "HashSet",
        "Console", "Guid", "DateTime", "TimeSpan", "Path", "File", "Directory", "Stream", "Encoding", "StringBuilder", "Attribute", "Enum", "Convert", "Thread", "Timer",
        "SerializeField", "Serializable", "Header", "Tooltip", "Range", "Editor", "EditorWindow", "AssetDatabase", "EditorGUILayout", "EditorGUI", "EditorUtility", "MenuItem",
        "Image", "Text", "Button", "Toggle", "Slider", "Canvas", "TMP_Text", "TextMeshProUGUI", "TextMeshPro", "float2", "float3", "float4", "quaternion", "math",
    };

    private static readonly HashSet<string> OtherWellKnownTypeNames = new(StringComparer.Ordinal)
    {
        "Physics2D", "Vector2Int", "Vector3Int", "RaycastHit", "Behaviour", "RenderTexture", "MeshFilter", "MeshRenderer", "Animator", "Animation", "AudioSource", "AudioClip",
        "AudioListener", "Cursor", "Event", "GUI", "GUILayout", "GUIContent", "GUIStyle", "Font", "Sprite", "WaitForSeconds", "WaitForEndOfFrame", "JsonUtility", "PlayerPrefs",
        "SystemInfo", "Graphics", "GL", "Plane", "Touch", "KeyCode", "LayerMask", "Rigidbody", "Rigidbody2D", "Collider", "Collider2D", "BoxCollider", "SphereCollider",
        "CapsuleCollider", "MeshCollider", "CharacterController", "Terrain", "Tree", "Grid", "Gradient", "AnimationCurve", "Keyframe", "Space", "Selection", "Handles", "Logger",
        "ILogger", "LogType", "Profiler", "Hash128", "HideInInspector", "RequireComponent", "ExecuteInEditMode", "ExecuteAlways", "Dropdown", "InputField", "ScrollRect",
        "RawImage", "Selectable", "Graphic", "Mask", "Outline", "Shadow", "Scrollbar", "TMP_InputField", "TMP_Dropdown", "TMP_FontAsset", "MathF", "Tuple", "Nullable", "Lazy",
        "Progress", "Version", "Uri", "Delegate", "EventArgs", "EventHandler", "BitConverter", "Regex", "Queue", "Stack", "LinkedList", "IEnumerable", "IEnumerator",
        "IDisposable", "IComparable", "IEquatable", "Span", "Memory", "CancellationToken", "Interlocked", "Monitor", "Mutex", "Semaphore", "SemaphoreSlim", "Stopwatch",
        "Process", "HttpClient", "WebClient", "Socket", "IPAddress", "Vector", "Complex", "BigInteger", "Half", "Index", "int2", "int3", "int4", "float3x3", "float4x4", "half",
    };

    public Task<ProjectInventory> ScanProjectAsync(string unityProjectPath, string? unityEditorPath = null, CancellationToken ct = default) =>
        Task.Run(() => ScanProject(unityProjectPath, unityEditorPath, ct), ct);

    public async Task<IReadOnlyList<PackageIssue>> CheckPackageAsync(string unityProjectPath, string packageRoot, string? packageId = null, string? unityEditorPath = null, CancellationToken ct = default)
    {
        var inventory = await ScanProjectAsync(unityProjectPath, unityEditorPath, ct).ConfigureAwait(false);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
        var existing = inventory.Packages.FirstOrDefault(p => string.Equals(Path.TrimEndingDirectorySeparator(p.Root), root, StringComparison.OrdinalIgnoreCase));
        var id = packageId ?? existing?.Id ?? UnityPackageMap.ReadName(root) ?? Path.GetFileName(root);
        if (existing is null || !string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase))
            inventory = inventory.With(await Task.Run(() => InspectPackage(new ProjectPackageRef(id, root, PackageOrigin.Local), ct), ct).ConfigureAwait(false));
        return Analyze(inventory, new PackageAnalyzerOptions { Focus = new[] { id } });
    }

    public ProjectInventory ScanProject(string unityProjectPath, string? unityEditorPath = null, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        var project = Path.TrimEndingDirectorySeparator(Path.GetFullPath(unityProjectPath));
        var refs = UnityPackageMap.List(project);
        var inspected = new InspectedPackage[refs.Count];
        Parallel.For(0, refs.Count, new ParallelOptions { CancellationToken = ct }, k => inspected[k] = InspectPackage(refs[k], ct));
        var manifest = Path.Combine(project, "Packages", "manifest.json");
        var version = UnityProjectService.ReadUnityVersion(project);
        return new ProjectInventory
        {
            UnityProjectPath = project,
            UnityVersion = version == "unknown" ? null : version,
            Packages = inspected,
            ManifestDependencies = UnityPackageMap.ReadDependencies(manifest),
            RegistryScopes = ReadScopes(manifest),
            EngineNamespaces = string.IsNullOrWhiteSpace(unityEditorPath) ? null : EngineNamespaces(unityEditorPath),
            EngineDlls = string.IsNullOrWhiteSpace(unityEditorPath) ? null : EngineDlls(unityEditorPath),
            ScriptingDefines = ReadScriptingDefines(project),
            DuplicateEmbedded = FindDuplicateEmbedded(project),
            ScanTime = watch.Elapsed,
        };
    }

    public InspectedPackage InspectPackage(ProjectPackageRef package, CancellationToken ct = default)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Root));
        var isProject = package.Origin == PackageOrigin.Project;
        var result = new InspectedPackage { Ref = package with { Root = root }, PackageJson = isProject ? null : ReadPackageJson(root) };
        var sources = new List<(string Full, string Relative, long Size)>();
        var asmdefs = new List<(string Full, string Relative)>();
        var asmrefs = new List<(string Full, string Relative)>();
        var plugins = new List<(string Full, string Relative, long Size)>();
        var assetGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(folder).GetFileSystemInfos(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            var names = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
            var atRoot = string.Equals(folder, root, StringComparison.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                var name = entry.Name;
                if (IsHidden(name)) continue;
                var relative = Relative(root, entry.FullName);
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!isProject && IsJunk(name, atRoot, names))
                    {
                        result.JunkFolders.Add(relative);
                        continue;
                    }
                    if (!isProject && atRoot && string.Equals(name, "Samples", StringComparison.OrdinalIgnoreCase)) result.VisibleSampleFolders.Add(relative);
                    if (!isProject && !names.Contains(name + ".meta")) result.MissingMetas.Add(relative);
                    if (!IsOpaqueFolder(name)) pending.Push(entry.FullName);
                    continue;
                }
                if (++files > MaxFilesPerPackage)
                {
                    result.Truncated = true;
                    pending.Clear();
                    break;
                }
                var extension = Path.GetExtension(name);
                if (extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    if (ReadMetaGuid(entry.FullName) is { } guid)
                    {
                        var asset = relative[..^5];
                        if (!result.Guids.TryGetValue(guid, out var owners)) result.Guids[guid] = owners = new List<string>();
                        owners.Add(asset);
                        assetGuids[asset] = guid;
                    }
                    continue;
                }
                if (!isProject && !names.Contains(name + ".meta")) result.MissingMetas.Add(relative);
                if (atRoot && !isProject)
                {
                    if (name.StartsWith("README", StringComparison.OrdinalIgnoreCase)) result.HasReadme = true;
                    if (name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LICENCE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase))
                        result.HasLicenseFile = true;
                }
                var size = entry is FileInfo info ? info.Length : 0;
                if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)) sources.Add((entry.FullName, relative, size));
                else if (extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase)) asmdefs.Add((entry.FullName, relative));
                else if (extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase)) asmrefs.Add((entry.FullName, relative));
                else if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) plugins.Add((entry.FullName, relative, size));
            }
        }

        foreach (var (full, relative) in asmdefs) result.Assemblies.Add(ReadAsmdef(full, relative, assetGuids.GetValueOrDefault(relative)));
        foreach (var (full, relative) in asmrefs) result.Asmrefs.Add(ReadAsmref(full, relative));
        foreach (var (full, relative, size) in plugins) result.Plugins.Add(ReadPlugin(full, relative, size));

        var parsed = new InventorySource[sources.Count];
        Parallel.For(0, sources.Count, new ParallelOptions { CancellationToken = ct }, k =>
        {
            var (full, relative, size) = sources[k];
            CSharpSourceInfo info = CSharpSourceInfo.Empty;
            var tooLarge = size > MaxSourceBytes;
            if (!tooLarge)
            {
                try { info = CSharpDeclarationScanner.Scan(File.ReadAllText(full)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {full}", ex); }
            }
            parsed[k] = new InventorySource { FullPath = full, RelativePath = relative, Info = info, TooLarge = tooLarge };
        });
        result.Sources.AddRange(parsed);
        AssignOwners(result, isProject);
        return result;
    }

    private static readonly string[] OpaqueFolderExtensions = { ".bundle", ".framework", ".xcframework", ".plugin", ".app" };

    private static bool IsOpaqueFolder(string name) => OpaqueFolderExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static bool IsJunk(string name, bool atRoot, HashSet<string> siblings)
    {
        if (!JunkNames.Contains(name)) return false;
        if (string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
            return siblings.Any(s => s.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
        return atRoot;
    }

    private static void AssignOwners(InspectedPackage package, bool isProject)
    {
        var owners = new Dictionary<string, (string? Assembly, string? Asmref)>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdef in package.Assemblies)
            if (asmdef.Name.Length > 0) owners.TryAdd(asmdef.Folder, (asmdef.Name, null));
        foreach (var asmref in package.Asmrefs)
            owners.TryAdd(Path.GetDirectoryName(asmref.FullPath) ?? "", (null, asmref.Reference));
        foreach (var source in package.Sources)
        {
            var folder = Path.GetDirectoryName(source.FullPath);
            var found = false;
            while (folder is not null && folder.Length >= package.Root.Length)
            {
                if (owners.TryGetValue(folder, out var owner))
                {
                    source.Assembly = owner.Assembly;
                    source.AsmrefReference = owner.Asmref;
                    found = true;
                    break;
                }
                if (string.Equals(folder, package.Root, StringComparison.OrdinalIgnoreCase)) break;
                folder = Path.GetDirectoryName(folder);
            }
            if (found) continue;
            var inEditor = source.RelativePath.Split('/').SkipLast(1).Any(s => string.Equals(s, "Editor", StringComparison.OrdinalIgnoreCase));
            source.Assembly = inEditor ? EditorPredefined : isProject ? RuntimePredefined : null;
        }
    }

    public IReadOnlyList<PackageIssue> Analyze(ProjectInventory inventory, PackageAnalyzerOptions? options = null) =>
        new AnalyzerRun(inventory, options ?? new PackageAnalyzerOptions()).Run();

    public static bool IsHidden(string name) =>
        name.Length == 0 || name[0] == '.' || name[^1] == '~' || string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string full) => Path.GetRelativePath(root, full).Replace('\\', '/');

    private static readonly JsonDocumentOptions LenientJson = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static InventoryPackageJson? ReadPackageJson(string root)
    {
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), LenientJson);
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return new InventoryPackageJson { Error = "package.json isn't a JSON object." };
            var invalid = new List<string>();
            var samples = new List<string>();
            if (json.TryGetProperty("samples", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var sample in list.EnumerateArray())
                    if (sample.ValueKind == JsonValueKind.Object && Text(sample, "path") is { Length: > 0 } samplePath) samples.Add(samplePath);
            return new InventoryPackageJson
            {
                Name = Text(json, "name"),
                Version = Text(json, "version"),
                DisplayName = Text(json, "displayName"),
                Description = Text(json, "description"),
                Unity = Text(json, "unity"),
                UnityRelease = Text(json, "unityRelease"),
                License = Text(json, "license"),
                Dependencies = Map(json, "dependencies", invalid),
                VpmDependencies = Map(json, "vpmDependencies", null),
                SamplePaths = samples,
                InvalidDependencyValues = invalid,
            };
        }
        catch (JsonException ex) { return new InventoryPackageJson { Error = ex.Message }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new InventoryPackageJson { Error = ex.Message }; }
    }

    private static string? Text(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, string> Map(JsonElement json, string name, List<string>? invalid)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return map;
        foreach (var dep in value.EnumerateObject())
        {
            if (dep.Value.ValueKind == JsonValueKind.String) map[dep.Name] = dep.Value.GetString() ?? "";
            else
            {
                map[dep.Name] = dep.Value.ToString();
                invalid?.Add(dep.Name);
            }
        }
        return map;
    }

    public static AsmdefInfo ReadAsmdef(string full, string relative, string? guid)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(full), LenientJson);
            var json = doc.RootElement;
            if (json.ValueKind != JsonValueKind.Object) return new AsmdefInfo { Name = "", FullPath = full, RelativePath = relative, Guid = guid, Error = "isn't a JSON object" };
            var versionDefines = new List<AsmdefVersionDefine>();
            if (json.TryGetProperty("versionDefines", out var defines) && defines.ValueKind == JsonValueKind.Array)
                foreach (var define in defines.EnumerateArray())
                    if (define.ValueKind == JsonValueKind.Object && Text(define, "name") is { Length: > 0 } package && Text(define, "define") is { Length: > 0 } symbol)
                        versionDefines.Add(new AsmdefVersionDefine(package, Text(define, "expression") ?? "", symbol));
            return new AsmdefInfo
            {
                Name = Text(json, "name")?.Trim() ?? "",
                FullPath = full,
                RelativePath = relative,
                Guid = guid,
                Error = Text(json, "name") is { Length: > 0 } ? null : "has no name",
                References = Strings(json, "references"),
                IncludePlatforms = Strings(json, "includePlatforms"),
                ExcludePlatforms = Strings(json, "excludePlatforms"),
                DefineConstraints = Strings(json, "defineConstraints"),
                PrecompiledReferences = Strings(json, "precompiledReferences"),
                OptionalUnityReferences = Strings(json, "optionalUnityReferences"),
                VersionDefines = versionDefines,
                AutoReferenced = !json.TryGetProperty("autoReferenced", out var auto) || auto.ValueKind != JsonValueKind.False,
                OverrideReferences = json.TryGetProperty("overrideReferences", out var over) && over.ValueKind == JsonValueKind.True,
                NoEngineReferences = json.TryGetProperty("noEngineReferences", out var engine) && engine.ValueKind == JsonValueKind.True,
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AsmdefInfo { Name = "", FullPath = full, RelativePath = relative, Guid = guid, Error = ex is JsonException ? "isn't valid JSON" : ex.Message };
        }
    }

    private static List<string> Strings(JsonElement json, string name)
    {
        var list = new List<string>();
        if (json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } text) list.Add(text.Trim());
        return list;
    }

    private static AsmrefInfo ReadAsmref(string full, string relative)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(full), LenientJson);
            return new AsmrefInfo(full, relative, doc.RootElement.ValueKind == JsonValueKind.Object ? Text(doc.RootElement, "reference")?.Trim() ?? "" : "");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new AsmrefInfo(full, relative, ""); }
    }

    private static InventoryPlugin ReadPlugin(string full, string relative, long size)
    {
        var name = Path.GetFileName(full);
        var auto = true;
        try
        {
            var meta = full + ".meta";
            if (File.Exists(meta) && Regex.IsMatch(File.ReadAllText(meta), @"^\s*isExplicitlyReferenced:\s*1\s*$", RegexOptions.Multiline)) auto = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (size > MaxAssemblyBytes) return new InventoryPlugin { FileName = name, FullPath = full, RelativePath = relative, Size = size, AutoReferenced = auto };
        try
        {
            using var stream = File.OpenRead(full);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return new InventoryPlugin { FileName = name, FullPath = full, RelativePath = relative, Size = size, AutoReferenced = auto };
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly) return new InventoryPlugin { FileName = name, FullPath = full, RelativePath = relative, Size = size, AutoReferenced = auto };
            var (namespaces, types) = PublicTypes(reader);
            return new InventoryPlugin
            {
                FileName = name,
                FullPath = full,
                RelativePath = relative,
                Size = size,
                IsManaged = true,
                AssemblyName = reader.GetString(reader.GetAssemblyDefinition().Name),
                AutoReferenced = auto,
                Namespaces = namespaces,
                PublicTypes = types,
            };
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new InventoryPlugin { FileName = name, FullPath = full, RelativePath = relative, Size = size, AutoReferenced = auto };
        }
    }

    private static (List<string> Namespaces, List<string> Types) PublicTypes(MetadataReader reader)
    {
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        var types = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!type.GetDeclaringType().IsNil) continue;
            var ns = reader.GetString(type.Namespace);
            var typeName = reader.GetString(type.Name);
            if (typeName.StartsWith('<')) continue;
            if (ns.Length > 0) AddWithPrefixes(namespaces, ns);
            if ((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public) types.Add(ns.Length == 0 ? typeName : ns + "." + typeName);
        }
        return (namespaces.ToList(), types);
    }

    private static void AddWithPrefixes(ISet<string> set, string ns)
    {
        var dot = -1;
        while ((dot = ns.IndexOf('.', dot + 1)) >= 0) set.Add(ns[..dot]);
        set.Add(ns);
    }

    public static IReadOnlySet<string>? EngineNamespaces(string unityEditorPath)
    {
        var editor = File.Exists(unityEditorPath) ? Path.GetDirectoryName(unityEditorPath) ?? unityEditorPath : unityEditorPath;
        var managed = Path.Combine(editor, "Data", "Managed");
        if (!Directory.Exists(managed))
        {
            var mac = Path.Combine(editor, "Contents", "Managed");
            if (!Directory.Exists(mac)) return null;
            managed = mac;
        }
        return EngineNamespaceCache.GetOrAdd(managed, folder =>
        {
            var set = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var dlls = Directory.EnumerateFiles(folder, "*.dll").Concat(Directory.Exists(Path.Combine(folder, "UnityEngine")) ? Directory.EnumerateFiles(Path.Combine(folder, "UnityEngine"), "*.dll") : Array.Empty<string>()).ToList();
            Parallel.ForEach(dlls, dll =>
            {
                try
                {
                    using var stream = File.OpenRead(dll);
                    using var pe = new PEReader(stream);
                    if (!pe.HasMetadata) return;
                    var reader = pe.GetMetadataReader();
                    var local = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var ns in PublicTypes(reader).Namespaces) local.Add(ns);
                    foreach (var ns in local) set.TryAdd(ns, 0);
                }
                catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            });
            return set.Keys.ToHashSet(StringComparer.Ordinal);
        });
    }

    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> EngineDllCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string>? EngineDlls(string unityEditorPath)
    {
        var editor = File.Exists(unityEditorPath) ? Path.GetDirectoryName(unityEditorPath) ?? unityEditorPath : unityEditorPath;
        var data = Path.Combine(editor, "Data");
        if (!Directory.Exists(data)) data = Path.Combine(editor, "Contents");
        if (!Directory.Exists(data)) return null;
        return EngineDllCache.GetOrAdd(data, folder =>
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sub in new[] { "NetStandard", "BCLExtensions", "Managed" })
            {
                var path = Path.Combine(folder, sub);
                if (!Directory.Exists(path)) continue;
                try { foreach (var dll in Directory.EnumerateFiles(path, "*.dll", SearchOption.AllDirectories)) names.Add(Path.GetFileName(dll)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Listing {path}", ex); }
            }
            return names;
        });
    }

    private static string? ReadMetaGuid(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 512);
            var buffer = new byte[256];
            var read = stream.Read(buffer, 0, buffer.Length);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var match = GuidLine().Match(text);
            return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"(?m)^guid:\s*([0-9a-fA-F]{32})\s*$")]
    private static partial Regex GuidLine();

    private static List<InventoryRegistryScope> ReadScopes(string manifestPath)
    {
        var scopes = new List<InventoryRegistryScope>();
        if (!File.Exists(manifestPath)) return scopes;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath), LenientJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("scopedRegistries", out var registries) && registries.ValueKind == JsonValueKind.Array)
                foreach (var registry in registries.EnumerateArray())
                    if (registry.ValueKind == JsonValueKind.Object)
                        foreach (var scope in Strings(registry, "scopes")) scopes.Add(new InventoryRegistryScope(Text(registry, "name") ?? Text(registry, "url") ?? "", scope));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading the scoped registries in {manifestPath}", ex); }
        return scopes;
    }

    public static HashSet<string> ReadScriptingDefines(string unityProjectPath)
    {
        var defines = new HashSet<string>(StringComparer.Ordinal);
        var path = Path.Combine(unityProjectPath, "ProjectSettings", "ProjectSettings.asset");
        if (!File.Exists(path)) return defines;
        try
        {
            var inBlock = false;
            var indent = -1;
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.TrimStart();
                var depth = line.Length - trimmed.Length;
                if (!inBlock)
                {
                    if (trimmed.StartsWith("scriptingDefineSymbols:", StringComparison.Ordinal))
                    {
                        inBlock = true;
                        indent = depth;
                        var inline = trimmed["scriptingDefineSymbols:".Length..].Trim();
                        if (inline.Length > 0 && inline != "{}") AddDefines(defines, inline);
                    }
                    continue;
                }
                if (trimmed.Length == 0) continue;
                if (depth <= indent) break;
                var colon = trimmed.IndexOf(':');
                if (colon >= 0) AddDefines(defines, trimmed[(colon + 1)..]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading the scripting defines of {unityProjectPath}", ex); }
        return defines;
    }

    private static void AddDefines(HashSet<string> defines, string list)
    {
        foreach (var symbol in list.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) defines.Add(symbol);
    }

    private static List<(string Id, string First, string Second)> FindDuplicateEmbedded(string unityProjectPath)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<(string, string, string)>();
        var packages = Path.Combine(unityProjectPath, "Packages");
        if (!Directory.Exists(packages)) return duplicates;
        foreach (var folder in Directory.EnumerateDirectories(packages).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(folder, "package.json")) || UnityPackageMap.ReadName(folder) is not { } name) continue;
            var relative = "Packages/" + Path.GetFileName(folder);
            if (seen.TryGetValue(name, out var first)) duplicates.Add((name, first, relative));
            else seen[name] = relative;
        }
        return duplicates;
    }
}
