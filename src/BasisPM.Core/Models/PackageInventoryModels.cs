namespace BasisPM.Core.Models;

public sealed record CSharpTypeDeclaration(string Namespace, string Name, int Arity, string Kind, bool IsPublic, bool IsFileLocal, bool IsConditional, int Line)
{
    public string FullName => Namespace.Length == 0 ? Name : Namespace + "." + Name;
    public string MetadataName => Arity == 0 ? FullName : $"{FullName}`{Arity}";
}

public sealed record CSharpEditorUse(int Line, string Name);

public sealed record CSharpUsingDirective(string Target, string? Alias, bool IsStatic, bool IsGlobal, bool IsConditional, int Line)
{
    public string Scope { get; init; } = "";
}

public sealed record CSharpSourceInfo(IReadOnlyList<CSharpTypeDeclaration> Types, IReadOnlyList<string> Namespaces, IReadOnlyList<CSharpUsingDirective> Usings, IReadOnlyList<int> UnguardedEditorLines)
{
    public IReadOnlyList<int> UnguardedEditorUsings { get; init; } = Array.Empty<int>();
    public IReadOnlyList<CSharpEditorUse> EditorTypeUses { get; init; } = Array.Empty<CSharpEditorUse>();

    public static CSharpSourceInfo Empty { get; } = new(Array.Empty<CSharpTypeDeclaration>(), Array.Empty<string>(), Array.Empty<CSharpUsingDirective>(), Array.Empty<int>());
}

public sealed record AsmdefVersionDefine(string Package, string Expression, string Define);

public sealed class AsmdefInfo
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public string? Guid { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludePlatforms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludePlatforms { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DefineConstraints { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PrecompiledReferences { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> OptionalUnityReferences { get; init; } = Array.Empty<string>();
    public IReadOnlyList<AsmdefVersionDefine> VersionDefines { get; init; } = Array.Empty<AsmdefVersionDefine>();
    public bool AutoReferenced { get; init; } = true;
    public bool OverrideReferences { get; init; }
    public bool NoEngineReferences { get; init; }

    public string Folder => Path.GetDirectoryName(FullPath) ?? "";

    public bool IsEditorOnly =>
        (IncludePlatforms.Count > 0 && IncludePlatforms.All(p => string.Equals(p, "Editor", StringComparison.OrdinalIgnoreCase)))
        || DefineConstraints.Any(d => d.Split("||").All(alternative => alternative.Trim().StartsWith("UNITY_EDITOR", StringComparison.Ordinal)));

    public bool IsTestAssembly =>
        References.Any(r => r is "UnityEngine.TestRunner" or "UnityEditor.TestRunner" or "GUID:27619889b8ba8c24980f49ee34dbb44a" or "GUID:0acc523941302664db1f4e527237feb3")
        || PrecompiledReferences.Any(r => string.Equals(r, "nunit.framework.dll", StringComparison.OrdinalIgnoreCase))
        || OptionalUnityReferences.Any(r => string.Equals(r, "TestAssemblies", StringComparison.OrdinalIgnoreCase));
}

public sealed record AsmrefInfo(string FullPath, string RelativePath, string Reference);

public sealed class InventorySource
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public string? Assembly { get; set; }
    public string? AsmrefReference { get; set; }
    public bool TooLarge { get; init; }
    public CSharpSourceInfo Info { get; init; } = CSharpSourceInfo.Empty;
}

public sealed class InventoryPlugin
{
    public required string FileName { get; init; }
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
    public bool IsManaged { get; init; }
    public string? AssemblyName { get; init; }
    public bool AutoReferenced { get; init; } = true;
    public IReadOnlyList<string> Namespaces { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PublicTypes { get; init; } = Array.Empty<string>();
}

public sealed class InventoryPackageJson
{
    public string? Error { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string? Unity { get; init; }
    public string? UnityRelease { get; init; }
    public string? License { get; init; }
    public IReadOnlyDictionary<string, string> Dependencies { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> VpmDependencies { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> SamplePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InvalidDependencyValues { get; init; } = Array.Empty<string>();
}

public sealed class InspectedPackage
{
    public required ProjectPackageRef Ref { get; init; }
    public InventoryPackageJson? PackageJson { get; init; }
    public List<AsmdefInfo> Assemblies { get; } = new();
    public List<AsmrefInfo> Asmrefs { get; } = new();
    public List<InventorySource> Sources { get; } = new();
    public List<InventoryPlugin> Plugins { get; } = new();
    public Dictionary<string, List<string>> Guids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> MissingMetas { get; } = new();
    public List<string> JunkFolders { get; } = new();
    public List<string> VisibleSampleFolders { get; } = new();
    public bool HasReadme { get; set; }
    public bool HasLicenseFile { get; set; }
    public bool Truncated { get; set; }

    public string Id => Ref.Id;
    public string Root => Ref.Root;
    public PackageOrigin Origin => Ref.Origin;
    public bool IsUnityPackage => Id.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase);
}

public sealed record InventoryRegistryScope(string Registry, string Scope);

public sealed class ProjectInventory
{
    public required string UnityProjectPath { get; init; }
    public string? UnityVersion { get; init; }
    public IReadOnlyList<InspectedPackage> Packages { get; init; } = Array.Empty<InspectedPackage>();
    public IReadOnlyDictionary<string, string> ManifestDependencies { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<InventoryRegistryScope> RegistryScopes { get; init; } = Array.Empty<InventoryRegistryScope>();
    public IReadOnlySet<string>? EngineNamespaces { get; init; }
    public IReadOnlySet<string>? EngineDlls { get; init; }
    public IReadOnlySet<string> ScriptingDefines { get; init; } = new HashSet<string>();
    public IReadOnlyList<(string Id, string First, string Second)> DuplicateEmbedded { get; init; } = Array.Empty<(string, string, string)>();
    public TimeSpan ScanTime { get; init; }

    public InspectedPackage? Find(string id) => Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public ProjectInventory With(InspectedPackage package)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(package.Root));
        var list = Packages.Where(p => !string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p.Root)), root, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(package);
        return new ProjectInventory
        {
            UnityProjectPath = UnityProjectPath,
            UnityVersion = UnityVersion,
            Packages = list,
            ManifestDependencies = ManifestDependencies,
            RegistryScopes = RegistryScopes,
            EngineNamespaces = EngineNamespaces,
            EngineDlls = EngineDlls,
            ScriptingDefines = ScriptingDefines,
            DuplicateEmbedded = DuplicateEmbedded,
            ScanTime = ScanTime,
        };
    }
}

public sealed class PackageAnalyzerOptions
{
    public IReadOnlyCollection<string>? Focus { get; init; }
    public IReadOnlyCollection<string>? Installable { get; init; }
}
