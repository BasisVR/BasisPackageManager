using System.Security.Cryptography;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed partial class PackageAnalyzer
{
    private enum AnalyzerAssemblyKind { Asmdef, Predefined, Plugin }

    private enum AnalyzerCompile { No, Maybe, Definite }

    private sealed class AnalyzerAssembly
    {
        public required string Name { get; init; }
        public required InspectedPackage Package { get; init; }
        public AnalyzerAssemblyKind Kind { get; init; }
        public AsmdefInfo? Asmdef { get; init; }
        public InventoryPlugin? Plugin { get; init; }
        public bool Editor { get; set; }
        public bool AutoReferenced { get; set; }
        public AnalyzerCompile Compile { get; set; } = AnalyzerCompile.Definite;
        public List<(InventorySource Source, InspectedPackage Package)> Sources { get; } = new();
        public HashSet<string> Namespaces { get; } = new(StringComparer.Ordinal);
        public HashSet<AnalyzerAssembly> Visible { get; } = new();
        public HashSet<AnalyzerAssembly> Consumers { get; } = new();
        public bool NoEngine => Asmdef?.NoEngineReferences == true;
        public string Display => Kind == AnalyzerAssemblyKind.Plugin ? Plugin!.FileName : Name;
    }

    private sealed record AnalyzerType(AnalyzerAssembly Assembly, string FullName, string Name, string Namespace, InventorySource? Source, int Line);

    private sealed class AnalyzerRun
    {
        private readonly ProjectInventory inventory;
        private readonly PackageAnalyzerOptions options;
        private readonly List<PackageIssue> issues = new();
        private readonly HashSet<string> focus;
        private readonly HashSet<string> installable;
        private readonly HashSet<string> present;
        private readonly List<AnalyzerAssembly> assemblies = new();
        private readonly Dictionary<string, AnalyzerAssembly> byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AnalyzerAssembly> byGuid = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(InspectedPackage, string), AnalyzerAssembly> byPackageName = new();
        private readonly Dictionary<string, List<(AsmdefInfo Asmdef, InspectedPackage Package)>> asmdefNames = new(StringComparer.Ordinal);
        private readonly List<AnalyzerAssembly> plugins = new();
        private readonly Dictionary<string, List<AnalyzerAssembly>> namespaceOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AnalyzerType>> typeOwners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<AnalyzerType>> namespacedBySimpleName = new(StringComparer.Ordinal);
        private AnalyzerAssembly runtime = null!, editor = null!;

        public AnalyzerRun(ProjectInventory inventory, PackageAnalyzerOptions options)
        {
            this.inventory = inventory;
            this.options = options;
            focus = options.Focus is { } wanted
                ? new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase)
                : inventory.Packages.Where(p => p.Origin != PackageOrigin.Project && !p.IsUnityPackage).Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            installable = new HashSet<string>(options.Installable ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            present = inventory.Packages.Select(p => p.Id).Concat(inventory.ManifestDependencies.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private bool InFocus(InspectedPackage package) => focus.Contains(package.Id);

        public IReadOnlyList<PackageIssue> Run()
        {
            BuildAssemblies();
            ResolveVisibility();
            BuildIndexes();
            foreach (var package in inventory.Packages)
                if (InFocus(package) && package.Origin != PackageOrigin.Project) PackageRules(package);
            AssemblyRules();
            TypeRules();
            DllRules();
            GuidRules();
            EmbeddedRules();
            ReferenceRules();
            NamespaceRules();
            return issues
                .DistinctBy(i => (i.Code, i.Package, i.File, i.Line, i.Message))
                .OrderByDescending(i => i.Severity)
                .ThenBy(i => i.Package, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Code, StringComparer.Ordinal)
                .ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Line ?? 0)
                .ToList();
        }

        private void Add(string code, IssueSeverity severity, string package, string message, string? file = null, int? line = null, string? other = null, string? fix = null) =>
            issues.Add(new PackageIssue(code, severity, package, message) { File = file, Line = line, Other = other, Fix = fix });

        private string Show(string full)
        {
            var relative = Path.GetRelativePath(inventory.UnityProjectPath, full);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full.Replace('\\', '/') : relative.Replace('\\', '/');
        }

        private string Show(InspectedPackage package, string relative) => Show(Path.Combine(package.Root, relative));

        private static string Count(int count, string one, string many) => count == 1 ? "1 " + one : $"{count} {many}";

        private static string List(IEnumerable<string> items, int limit = MaxFilesPerIssue)
        {
            var all = items.ToList();
            var shown = string.Join(", ", all.Take(limit));
            return all.Count > limit ? $"{shown} and {all.Count - limit} more" : shown;
        }

        private void BuildAssemblies()
        {
            var assets = inventory.Packages.FirstOrDefault(p => p.Origin == PackageOrigin.Project)
                ?? new InspectedPackage { Ref = new ProjectPackageRef(UnityPackageMap.AssetsId, Path.Combine(inventory.UnityProjectPath, "Assets"), PackageOrigin.Project) };
            runtime = new AnalyzerAssembly { Name = RuntimePredefined, Package = assets, Kind = AnalyzerAssemblyKind.Predefined };
            editor = new AnalyzerAssembly { Name = EditorPredefined, Package = assets, Kind = AnalyzerAssemblyKind.Predefined, Editor = true };
            assemblies.Add(runtime);
            assemblies.Add(editor);

            foreach (var package in inventory.Packages)
            foreach (var asmdef in package.Assemblies)
            {
                if (asmdef.Error is not null || asmdef.Name.Length == 0) continue;
                if (!asmdefNames.TryGetValue(asmdef.Name, out var named)) asmdefNames[asmdef.Name] = named = new();
                named.Add((asmdef, package));
                if (PredefinedNames.Contains(asmdef.Name, StringComparer.OrdinalIgnoreCase) || byPackageName.ContainsKey((package, asmdef.Name))) continue;
                var assembly = new AnalyzerAssembly
                {
                    Name = asmdef.Name, Package = package, Kind = AnalyzerAssemblyKind.Asmdef, Asmdef = asmdef, Editor = asmdef.IsEditorOnly, AutoReferenced = asmdef.AutoReferenced,
                };
                assembly.Compile = Evaluate(asmdef);
                byPackageName[(package, asmdef.Name)] = assembly;
                byName.TryAdd(asmdef.Name, assembly);
                if (asmdef.Guid is { } guid) byGuid.TryAdd(guid, assembly);
                assemblies.Add(assembly);
            }

            foreach (var package in inventory.Packages)
            foreach (var source in package.Sources)
            {
                AnalyzerAssembly? owner = null;
                if (source.Assembly == RuntimePredefined) owner = runtime;
                else if (source.Assembly == EditorPredefined) owner = editor;
                else if (source.Assembly is { } name) owner = byPackageName.GetValueOrDefault((package, name)) ?? byName.GetValueOrDefault(name);
                else if (source.AsmrefReference is { Length: > 0 } reference) owner = Resolve(reference);
                owner?.Sources.Add((source, package));
            }

            foreach (var package in inventory.Packages)
            foreach (var plugin in package.Plugins)
            {
                if (!plugin.IsManaged) continue;
                var assembly = new AnalyzerAssembly
                {
                    Name = plugin.AssemblyName ?? Path.GetFileNameWithoutExtension(plugin.FileName), Package = package, Kind = AnalyzerAssemblyKind.Plugin, Plugin = plugin,
                    AutoReferenced = plugin.AutoReferenced,
                };
                foreach (var ns in plugin.Namespaces) assembly.Namespaces.Add(ns);
                plugins.Add(assembly);
                assemblies.Add(assembly);
            }

            foreach (var assembly in assemblies)
            {
                if (assembly.Kind == AnalyzerAssemblyKind.Plugin) continue;
                foreach (var (source, _) in assembly.Sources)
                foreach (var ns in source.Info.Namespaces) assembly.Namespaces.Add(ns);
                if (assembly.Kind == AnalyzerAssemblyKind.Asmdef && assembly.Sources.Count == 0) assembly.Compile = AnalyzerCompile.No;
            }
        }

        private AnalyzerAssembly? Resolve(string reference)
        {
            if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase)) return byGuid.GetValueOrDefault(reference[5..].Trim());
            return byName.GetValueOrDefault(reference);
        }

        private AnalyzerCompile Evaluate(AsmdefInfo asmdef)
        {
            if (asmdef.IncludePlatforms.Count > 0 && !asmdef.IncludePlatforms.Contains("Editor", StringComparer.OrdinalIgnoreCase)) return AnalyzerCompile.No;
            if (asmdef.ExcludePlatforms.Contains("Editor", StringComparer.OrdinalIgnoreCase)) return AnalyzerCompile.No;
            if (asmdef.DefineConstraints.Count == 0) return AnalyzerCompile.Definite;
            var defines = new HashSet<string>(inventory.ScriptingDefines, StringComparer.Ordinal) { "UNITY_EDITOR" };
            foreach (var versionDefine in asmdef.VersionDefines)
                if (string.Equals(versionDefine.Package, "Unity", StringComparison.OrdinalIgnoreCase) || present.Contains(versionDefine.Package)) defines.Add(versionDefine.Define);
            var state = AnalyzerCompile.Definite;
            foreach (var entry in asmdef.DefineConstraints)
            {
                var entryState = AnalyzerCompile.No;
                foreach (var raw in entry.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var negated = raw.StartsWith('!');
                    var symbol = negated ? raw[1..].Trim() : raw;
                    var known = SymbolState(symbol, defines);
                    var alternative = known switch
                    {
                        AnalyzerCompile.Definite => negated ? AnalyzerCompile.No : AnalyzerCompile.Definite,
                        AnalyzerCompile.No => negated ? AnalyzerCompile.Definite : AnalyzerCompile.No,
                        _ => AnalyzerCompile.Maybe,
                    };
                    if (alternative > entryState) entryState = alternative;
                    if (entryState == AnalyzerCompile.Definite) break;
                }
                if (entryState < state) state = entryState;
            }
            return state;
        }

        private AnalyzerCompile SymbolState(string symbol, HashSet<string> defines)
        {
            if (defines.Contains(symbol)) return AnalyzerCompile.Definite;
            var newer = Regex.Match(symbol, @"^UNITY_(\d+)(?:_(\d+))?_OR_NEWER$");
            if (newer.Success && UnityVersion.TryParse(inventory.UnityVersion, out var project))
            {
                var major = int.Parse(newer.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var minor = newer.Groups[2].Success ? int.Parse(newer.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                return project.Major > major || (project.Major == major && project.Minor >= minor) ? AnalyzerCompile.Definite : AnalyzerCompile.No;
            }
            if (symbol.StartsWith("UNITY_", StringComparison.Ordinal) || symbol.StartsWith("ENABLE_", StringComparison.Ordinal) || symbol.StartsWith("PLATFORM_", StringComparison.Ordinal)
                || symbol.StartsWith("NET_", StringComparison.Ordinal) || symbol.StartsWith("CSHARP_", StringComparison.Ordinal) || symbol.StartsWith("UNITY", StringComparison.Ordinal)
                || symbol is "DEVELOPMENT_BUILD" or "DEBUG" or "TRACE")
                return AnalyzerCompile.Maybe;
            return AnalyzerCompile.No;
        }

        private void ResolveVisibility()
        {
            foreach (var assembly in assemblies.Where(a => a.Kind == AnalyzerAssemblyKind.Asmdef))
            {
                var asmdef = assembly.Asmdef!;
                foreach (var reference in asmdef.References)
                    if (Resolve(reference) is { } target && target != assembly) assembly.Visible.Add(target);
                if (asmdef.OptionalUnityReferences.Any(r => string.Equals(r, "TestAssemblies", StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var name in new[] { "UnityEngine.TestRunner", "UnityEditor.TestRunner" })
                        if (byName.GetValueOrDefault(name) is { } runner) assembly.Visible.Add(runner);
                    foreach (var nunit in plugins.Where(p => string.Equals(p.Plugin!.FileName, "nunit.framework.dll", StringComparison.OrdinalIgnoreCase))) assembly.Visible.Add(nunit);
                }
                foreach (var plugin in plugins)
                {
                    var listed = asmdef.PrecompiledReferences.Contains(plugin.Plugin!.FileName, StringComparer.OrdinalIgnoreCase);
                    if (asmdef.OverrideReferences ? listed : plugin.AutoReferenced || listed) assembly.Visible.Add(plugin);
                }
            }
            if (UnityVersion.TryParse(inventory.UnityVersion, out var unity) && unity.Major >= 6000)
            {
                var implicitUi = new[] { "UnityEngine.UI", "UnityEditor.UI" }.Select(n => byName.GetValueOrDefault(n)).OfType<AnalyzerAssembly>().ToList();
                foreach (var assembly in assemblies.Where(a => a.Kind != AnalyzerAssemblyKind.Plugin && !a.NoEngine))
                foreach (var ui in implicitUi)
                    if (ui != assembly) assembly.Visible.Add(ui);
            }
            foreach (var assembly in assemblies.Where(a => a.Kind == AnalyzerAssemblyKind.Asmdef && a.AutoReferenced && a.Compile != AnalyzerCompile.No))
            {
                if (!assembly.Editor) runtime.Visible.Add(assembly);
                editor.Visible.Add(assembly);
            }
            foreach (var plugin in plugins.Where(p => p.AutoReferenced))
            {
                runtime.Visible.Add(plugin);
                editor.Visible.Add(plugin);
            }
            editor.Visible.Add(runtime);
            foreach (var assembly in assemblies)
            foreach (var visible in assembly.Visible) visible.Consumers.Add(assembly);
        }

        private void BuildIndexes()
        {
            foreach (var assembly in assemblies)
            {
                if (assembly.Compile == AnalyzerCompile.No) continue;
                foreach (var ns in assembly.Namespaces)
                {
                    if (!namespaceOwners.TryGetValue(ns, out var owners)) namespaceOwners[ns] = owners = new();
                    owners.Add(assembly);
                }
                IEnumerable<AnalyzerType> types = assembly.Kind == AnalyzerAssemblyKind.Plugin
                    ? assembly.Plugin!.PublicTypes.Select(full => PluginType(assembly, full))
                    : assembly.Sources.SelectMany(s => s.Source.Info.Types
                        .Where(t => t.IsPublic && !t.IsFileLocal && !t.IsConditional)
                        .Select(t => new AnalyzerType(assembly, t.MetadataName, t.Name, t.Namespace, s.Source, t.Line)));
                foreach (var type in types.DistinctBy(t => t.FullName))
                {
                    if (!typeOwners.TryGetValue(type.FullName, out var owners)) typeOwners[type.FullName] = owners = new();
                    owners.Add(type);
                    if (type.Namespace.Length > 0)
                    {
                        if (!namespacedBySimpleName.TryGetValue(type.Name, out var simple)) namespacedBySimpleName[type.Name] = simple = new();
                        simple.Add(type);
                    }
                }
            }
        }

        private static AnalyzerType PluginType(AnalyzerAssembly assembly, string full)
        {
            var dot = full.LastIndexOf('.');
            var name = dot < 0 ? full : full[(dot + 1)..];
            var tick = name.IndexOf('`');
            return new AnalyzerType(assembly, full, tick < 0 ? name : name[..tick], dot < 0 ? "" : full[..dot], null, 0);
        }

        private void PackageRules(InspectedPackage package)
        {
            var id = package.Id;
            var json = package.PackageJson;
            if (json is null)
                Add(IssueCodes.NotAPackage, IssueSeverity.Error, id, $"{id} has no package.json at {Show(package.Root)}, so Unity can't load it as a package.",
                    fix: "Point the install at the folder that holds package.json.");
            else if (json.Error is { } error)
                Add(IssueCodes.ManifestInvalid, IssueSeverity.Error, id, $"package.json can't be read ({error}), so Unity won't load {id}.", Show(package, "package.json"), fix: "Fix the JSON in package.json.");
            else ManifestRules(package, json);

            if (package.Truncated)
                Add(IssueCodes.JunkFolder, IssueSeverity.Info, id, $"{id} has more than {MaxFilesPerPackage} files, so only part of it was checked.");
            if (package.JunkFolders.Count > 0)
                Add(IssueCodes.JunkFolder, IssueSeverity.Warning, id, $"{id} contains generated folders ({List(package.JunkFolders)}) that Unity imports like any other content.",
                    Show(package, package.JunkFolders[0]), fix: "Delete them from the package and add them to its .gitignore.");
            foreach (var samples in package.VisibleSampleFolders)
                Add(IssueCodes.SamplesVisible, IssueSeverity.Warning, id, $"{id} has a {samples} folder, so Unity imports and compiles the samples in every project that installs it.",
                    Show(package, samples), fix: "Rename the folder to Samples~ and list each sample in package.json \"samples\".");
            if (package.MissingMetas.Count > 0)
            {
                var immutable = package.Ref.Immutable;
                var effect = immutable
                    ? "Unity ignores files without a .meta file in an immutable package, so they are missing in this project"
                    : "Unity creates them inside the package, which shows up as changes to its files";
                Add(IssueCodes.MetaMissing, immutable ? IssueSeverity.Warning : IssueSeverity.Info, id,
                    $"{Count(package.MissingMetas.Count, "file or folder", "files or folders")} in {id} {(package.MissingMetas.Count == 1 ? "has" : "have")} no .meta file (for example {List(package.MissingMetas, 3)}); {effect}.",
                    Show(package, package.MissingMetas[0]), fix: "Open the package in Unity once and commit the .meta files it creates.");
            }

            var loose = package.Sources.Where(s => s.Assembly is null && s.AsmrefReference is null).ToList();
            if (loose.Count > 0)
                Add(IssueCodes.ScriptsOutsideAssembly, IssueSeverity.Warning, id,
                    $"{Count(loose.Count, "script", "scripts")} in {id} {(loose.Count == 1 ? "isn't" : "aren't")} under an assembly definition, so Unity doesn't compile {(loose.Count == 1 ? "it" : "them")} (for example {List(loose.Select(s => s.RelativePath), 3)}).",
                    Show(package, loose[0].RelativePath), fix: "Add an .asmdef file to the folder that holds them.");
            var editorLoose = package.Sources.Where(s => s.Assembly == EditorPredefined).ToList();
            if (editorLoose.Count > 0)
                Add(IssueCodes.ScriptsOutsideAssembly, IssueSeverity.Info, id,
                    $"{Count(editorLoose.Count, "editor script", "editor scripts")} in {id} {(editorLoose.Count == 1 ? "has" : "have")} no assembly definition, so Unity compiles {(editorLoose.Count == 1 ? "it" : "them")} into Assembly-CSharp-Editor together with the project's own scripts (for example {List(editorLoose.Select(s => s.RelativePath), 3)}).",
                    Show(package, editorLoose[0].RelativePath), fix: "Give the Editor folder its own Editor-only .asmdef.");

            foreach (var asmdef in package.Assemblies)
            {
                if (asmdef.Error is { } problem)
                    Add(IssueCodes.AsmdefInvalid, IssueSeverity.Error, id, $"{asmdef.RelativePath} {problem}, so Unity can't compile the scripts it covers.", Show(package, asmdef.RelativePath),
                        fix: "Fix the assembly definition file.");
                else if (asmdef.IsTestAssembly && !asmdef.DefineConstraints.Any(d => d.Contains("UNITY_INCLUDE_TESTS", StringComparison.Ordinal)))
                    Add(IssueCodes.TestsNotConstrained, IssueSeverity.Warning, id,
                        $"{asmdef.Name} is a test assembly without UNITY_INCLUDE_TESTS in its define constraints, so its tests compile into every project that installs {id}.",
                        Show(package, asmdef.RelativePath), fix: "Add UNITY_INCLUDE_TESTS to defineConstraints in the test assembly definition.");
            }
            foreach (var group in package.Assemblies.GroupBy(a => a.Folder, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                Add(IssueCodes.AsmdefInvalid, IssueSeverity.Error, id,
                    $"{Show(group.Key)} has {group.Count()} assembly definitions ({List(group.Select(a => Path.GetFileName(a.FullPath)))}), and Unity allows only one per folder, so it stops compiling scripts.",
                    Show(group.First().FullPath), fix: "Keep one assembly definition per folder.");

            var runtimeEditor = 0;
            foreach (var source in package.Sources)
            {
                if (source.Assembly is null || (byPackageName.GetValueOrDefault((package, source.Assembly)) ?? byName.GetValueOrDefault(source.Assembly)) is not { Kind: AnalyzerAssemblyKind.Asmdef } owner) continue;
                if (owner.Editor || owner.Compile == AnalyzerCompile.No || owner.Package != package) continue;
                var uses = source.Info.UnguardedEditorLines.Select(line => (Line: line, Name: (string?)null))
                    .Concat(source.Info.EditorTypeUses.Where(use => !DeclaredFor(owner, use.Name)).Select(use => (use.Line, Name: (string?)use.Name)))
                    .OrderBy(use => use.Line)
                    .ToList();
                if (uses.Count == 0) continue;
                if (++runtimeEditor > MaxFilesPerIssue) continue;
                var first = uses[0];
                var what = first.Name is null ? "the UnityEditor namespace" : $"{first.Name} from UnityEditor";
                Add(IssueCodes.EditorCodeInRuntime, IssueSeverity.Error, id,
                    $"{source.RelativePath} uses {what} outside #if UNITY_EDITOR in {owner.Name}, which is also built into players, so player builds fail even though the editor compiles it.",
                    Show(package, source.RelativePath), first.Line, fix: "Wrap the editor code in #if UNITY_EDITOR, or move it into an Editor-only assembly definition.");
            }
            if (runtimeEditor > MaxFilesPerIssue)
                Add(IssueCodes.EditorCodeInRuntime, IssueSeverity.Error, id, $"{Count(runtimeEditor - MaxFilesPerIssue, "more script", "more scripts")} in {id} {(runtimeEditor - MaxFilesPerIssue == 1 ? "uses" : "use")} UnityEditor in assemblies that are built into players.");

            var owned = assemblies.Where(a => a.Kind != AnalyzerAssemblyKind.Plugin && a.Compile != AnalyzerCompile.No).ToList();
            var globals = new List<(string Name, bool Shared)>();
            var foreign = new List<(string FullName, string Root)>();
            foreach (var assembly in owned)
            foreach (var (source, sourcePackage) in assembly.Sources)
            {
                if (sourcePackage != package) continue;
                foreach (var type in source.Info.Types.Where(t => t.IsPublic && !t.IsFileLocal))
                {
                    if (type.Namespace.Length == 0) globals.Add((type.Name, assembly.Kind == AnalyzerAssemblyKind.Asmdef && assembly.AutoReferenced));
                    else if (!package.IsUnityPackage && ForeignRoot(type.Namespace) is { } root) foreign.Add((type.FullName, root));
                }
            }
            if (globals.Count > 0)
            {
                var names = globals.Select(g => g.Name).Distinct(StringComparer.Ordinal).ToList();
                Add(IssueCodes.GlobalNamespace, IssueSeverity.Info, id,
                    $"{Count(names.Count, "public type", "public types")} in {id} {(names.Count == 1 ? "is" : "are")} in the global namespace ({List(names, 5)}), where {(names.Count == 1 ? "it" : "they")} can clash with types of the same name in other packages and in the project.",
                    fix: "Put them in a namespace of your own, such as one named after the package.");
            }
            foreach (var group in foreign.GroupBy(f => f.Root))
            {
                var basis = group.Key == "Basis";
                if (basis && id.StartsWith("com.basis.", StringComparison.OrdinalIgnoreCase)) continue;
                var owner = basis ? "Basis" : group.Key is "System" or "Microsoft" ? ".NET" : "Unity";
                Add(IssueCodes.ForeignNamespace, IssueSeverity.Info, id,
                    $"{id} declares types in {owner}'s {group.Key} namespace ({List(group.Select(g => g.FullName).Distinct(), 3)}), so if {owner} adds a type with the same name, both stop compiling.",
                    fix: "Move them into a namespace of your own.");
            }
        }

        private Dictionary<string, HashSet<AnalyzerAssembly>>? declaredNames;

        private bool DeclaredFor(AnalyzerAssembly assembly, string name)
        {
            if (declaredNames is null)
            {
                declaredNames = new Dictionary<string, HashSet<AnalyzerAssembly>>(StringComparer.Ordinal);
                foreach (var owner in assemblies)
                {
                    var names = owner.Kind == AnalyzerAssemblyKind.Plugin
                        ? owner.Plugin!.PublicTypes.Select(t => PluginType(owner, t).Name)
                        : owner.Sources.SelectMany(s => s.Source.Info.Types.Select(t => t.Name));
                    foreach (var typeName in names)
                    {
                        if (!declaredNames.TryGetValue(typeName, out var set)) declaredNames[typeName] = set = new HashSet<AnalyzerAssembly>();
                        set.Add(owner);
                    }
                }
            }
            return declaredNames.TryGetValue(name, out var holders) && (holders.Contains(assembly) || holders.Overlaps(assembly.Visible));
        }

        private static string? ForeignRoot(string ns)
        {
            var root = ns.Split('.')[0];
            if (root == "Basis") return "Basis";
            return ForeignRoots.Contains(root, StringComparer.Ordinal) ? root : null;
        }

        private void ManifestRules(InspectedPackage package, InventoryPackageJson json)
        {
            var id = package.Id;
            var file = Show(package, "package.json");
            if (string.IsNullOrWhiteSpace(json.Name))
                Add(IssueCodes.NameInvalid, IssueSeverity.Error, id, "package.json has no name, so Unity can't load the package.", file, fix: "Add a name such as com.yourname.yourpackage.");
            else if (!PackageName().IsMatch(json.Name))
                Add(IssueCodes.NameInvalid, IssueSeverity.Warning, id, $"The package name \"{json.Name}\" doesn't follow Unity's rules (lowercase, reverse domain like com.company.package), so Unity may reject it.",
                    file, fix: "Use a lowercase name such as com.yourname.yourpackage.");
            else if (!string.Equals(json.Name, id, StringComparison.OrdinalIgnoreCase))
                Add(IssueCodes.NameMismatch, IssueSeverity.Warning, id, $"package.json names the package \"{json.Name}\", but it is installed as \"{id}\", so Unity registers it as \"{json.Name}\".",
                    file, fix: "Use the same name in package.json and in the project's manifest.");

            if (string.IsNullOrWhiteSpace(json.Version))
                Add(IssueCodes.VersionInvalid, IssueSeverity.Error, id, "package.json has no version, so Unity won't load the package.", file, fix: "Add a version such as 1.0.0.");
            else if (!StrictVersion().IsMatch(json.Version))
                Add(IssueCodes.VersionInvalid, IssueSeverity.Error, id, $"The version \"{json.Version}\" isn't a semantic version like 1.2.3, so Unity won't load the package.", file,
                    fix: "Use a version such as 1.2.3.");

            if (string.IsNullOrWhiteSpace(json.DisplayName))
                Add(IssueCodes.DisplayNameMissing, IssueSeverity.Info, id, "package.json has no displayName, so Unity's Package Manager shows the raw package name.", file);
            if (string.IsNullOrWhiteSpace(json.Description))
                Add(IssueCodes.DescriptionMissing, IssueSeverity.Info, id, "package.json has no description.", file);
            if (string.IsNullOrWhiteSpace(json.License) && !package.HasLicenseFile)
                Add(IssueCodes.LicenseMissing, IssueSeverity.Info, id, $"{id} doesn't state a license (no license in package.json and no LICENSE file), so it isn't clear how it may be used.", file,
                    fix: "Add a license field to package.json and a LICENSE file.");
            if (!package.HasReadme)
                Add(IssueCodes.ReadmeMissing, IssueSeverity.Info, id, $"{id} has no README.", fix: "Add a README.md that says what the package does and how to use it.");

            if (!string.IsNullOrWhiteSpace(json.Unity))
            {
                if (!UnityField().IsMatch(json.Unity.Trim()))
                    Add(IssueCodes.UnityInvalid, IssueSeverity.Warning, id, $"The unity field \"{json.Unity}\" should look like 2022.3 or 6000.0.", file);
                else if (!string.IsNullOrWhiteSpace(json.UnityRelease) && !UnityReleaseField().IsMatch(json.UnityRelease.Trim()))
                    Add(IssueCodes.UnityInvalid, IssueSeverity.Warning, id, $"The unityRelease field \"{json.UnityRelease}\" should look like 0f1 or 30f1.", file);
                else if (UnityVersion.TryParse(inventory.UnityVersion, out var project) && RequiresNewer(json.Unity.Trim(), json.UnityRelease?.Trim(), project))
                    Add(IssueCodes.UnityNewer, IssueSeverity.Warning, id,
                        $"{id} says it needs Unity {json.Unity.Trim()}{(string.IsNullOrWhiteSpace(json.UnityRelease) ? "" : "." + json.UnityRelease.Trim())}, but this project uses {inventory.UnityVersion}; Unity loads it anyway, but it may use features this version doesn't have.",
                        file, fix: "Use the Unity version the package asks for, or check that it works here.");
            }

            foreach (var (dependency, value) in json.Dependencies) DependencyRule(package, dependency, value, file, json.InvalidDependencyValues.Contains(dependency, StringComparer.OrdinalIgnoreCase));
            foreach (var (dependency, value) in json.VpmDependencies)
            {
                if (present.Contains(dependency) || json.Dependencies.ContainsKey(dependency)) continue;
                if (installable.Contains(dependency))
                    Add(IssueCodes.DependencyInstall, IssueSeverity.Info, id, $"{id} also needs {dependency} {value}, which BasisPM can install from the Basis registry.", file, other: dependency);
                else
                    Add(IssueCodes.VpmDependencyMissing, IssueSeverity.Warning, id,
                        $"{id} lists {dependency} {value} in vpmDependencies, which Unity's Package Manager ignores, and {dependency} isn't in this project, so code that needs it won't compile.",
                        file, other: dependency, fix: $"Install {dependency}.");
            }
        }

        private void DependencyRule(InspectedPackage package, string dependency, string value, string file, bool notText)
        {
            var id = package.Id;
            var trimmed = value.Trim();
            if (notText || trimmed.Length == 0)
            {
                Add(IssueCodes.DependencyVersionInvalid, IssueSeverity.Error, id,
                    $"package.json gives {dependency} the version \"{trimmed}\", which isn't a version number, so Unity refuses to resolve any package in the project.", file, other: dependency,
                    fix: "Use a version number such as 1.0.0.");
                return;
            }
            if (LooksLikeLocation(trimmed))
            {
                Add(IssueCodes.DependencyUrl, IssueSeverity.Error, id,
                    $"package.json gives {dependency} as \"{trimmed}\", but Unity only accepts version numbers there, so it refuses to resolve any package in the project.", file, other: dependency,
                    fix: $"Use a version number in package.json and add {dependency} to the project itself (BasisPM installs registry dependencies for you).");
                return;
            }
            if (!StrictVersion().IsMatch(trimmed))
            {
                Add(IssueCodes.DependencyVersionInvalid, IssueSeverity.Error, id,
                    $"package.json gives {dependency} the version \"{trimmed}\", which isn't a plain version like 1.2.3, so Unity refuses to resolve any package in the project.", file, other: dependency,
                    fix: "Use a plain version number; Unity treats it as the minimum version.");
                return;
            }
            if (present.Contains(dependency))
            {
                if (dependency.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)) return;
                var installed = inventory.Find(dependency)?.PackageJson?.Version;
                if (installed is not null && SemVer.TryParse(installed, out var have) && SemVer.TryParse(trimmed, out var need) && have.CompareTo(need) < 0)
                    Add(IssueCodes.DependencyTooOld, IssueSeverity.Warning, id, $"{id} needs {dependency} {trimmed} or newer, but this project has {installed}.", file, other: dependency,
                        fix: $"Update {dependency}.");
                return;
            }
            if (dependency.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase) || inventory.RegistryScopes.Any(s => InScope(dependency, s.Scope))) return;
            if (installable.Contains(dependency))
            {
                Add(IssueCodes.DependencyInstall, IssueSeverity.Info, id, $"{id} needs {dependency} {trimmed}, which BasisPM can install from the Basis registry.", file, other: dependency);
                return;
            }
            Add(IssueCodes.DependencyMissing, IssueSeverity.Error, id,
                $"{id} needs {dependency} {trimmed}, which isn't in this project and isn't a Unity package, so Unity stops resolving every package in the project until it is added.", file,
                other: dependency, fix: $"Install {dependency} first.");
        }

        private static bool InScope(string id, string scope) =>
            string.Equals(id, scope, StringComparison.OrdinalIgnoreCase) || id.StartsWith(scope.TrimEnd('.') + ".", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeLocation(string value) =>
            value.Contains("://", StringComparison.Ordinal) || value.StartsWith("git", StringComparison.OrdinalIgnoreCase) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || value.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) || value.Contains('/') || value.Contains('\\');

        private static bool RequiresNewer(string unity, string? release, UnityVersion project)
        {
            var parts = unity.Split('.');
            var major = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            var minor = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            if (project.Major != major) return project.Major < major;
            if (project.Minor != minor) return project.Minor < minor;
            return !string.IsNullOrWhiteSpace(release) && UnityVersion.TryParse($"{major}.{minor}.{release}", out var required) && project.CompareTo(required) < 0;
        }

        private void AssemblyRules()
        {
            foreach (var (name, group) in asmdefNames)
            {
                var predefined = PredefinedNames.Contains(name, StringComparer.OrdinalIgnoreCase);
                if (group.Count < 2 && !predefined) continue;
                foreach (var (asmdef, package) in group)
                {
                    if (!InFocus(package)) continue;
                    var others = group.Where(g => g.Asmdef != asmdef).Select(g => Show(g.Asmdef.FullPath)).ToList();
                    var message = predefined
                        ? $"{Show(asmdef.FullPath)} is named {name}, which is the name of one of Unity's own script assemblies, so Unity stops compiling scripts."
                        : $"{Show(asmdef.FullPath)} and {List(others, 3)} are {(others.Count == 1 ? "both" : "all")} named {name}, so Unity stops compiling scripts until each assembly name is unique.";
                    Add(IssueCodes.AssemblyDuplicate, IssueSeverity.Error, package.Id, message, Show(asmdef.FullPath),
                        other: group.Select(g => g.Package.Id).FirstOrDefault(p => !string.Equals(p, package.Id, StringComparison.OrdinalIgnoreCase)),
                        fix: "Rename one of the assembly definitions.");
                }
            }
        }

        private void TypeRules()
        {
            var pairs = new Dictionary<(AnalyzerAssembly, AnalyzerAssembly), List<string>>();
            foreach (var (fullName, owners) in typeOwners)
            {
                if (owners.Count < 2) continue;
                for (var a = 0; a < owners.Count; a++)
                for (var b = a + 1; b < owners.Count; b++)
                {
                    var (first, second) = (owners[a].Assembly, owners[b].Assembly);
                    if (first == second || first.Package == second.Package || (!InFocus(first.Package) && !InFocus(second.Package))) continue;
                    if (first.Kind == AnalyzerAssemblyKind.Plugin && second.Kind == AnalyzerAssemblyKind.Plugin
                        && string.Equals(first.Plugin!.FileName, second.Plugin!.FileName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!first.Consumers.Overlaps(second.Consumers)) continue;
                    var key = string.CompareOrdinal(first.Name, second.Name) <= 0 ? (first, second) : (second, first);
                    if (!pairs.TryGetValue(key, out var names)) pairs[key] = names = new();
                    names.Add(owners[a].Namespace.Length == 0 ? owners[a].Name : owners[a].Namespace + "." + owners[a].Name);
                }
            }
            foreach (var ((first, second), names) in pairs)
            {
                var reported = InFocus(first.Package) ? first : second;
                var other = reported == first ? second : first;
                var distinct = names.Distinct(StringComparer.Ordinal).ToList();
                Add(IssueCodes.TypeDuplicate, IssueSeverity.Warning, reported.Package.Id,
                    $"{List(distinct, 3)} {(distinct.Count == 1 ? "is" : "are")} declared in both {first.Display} ({first.Package.Id}) and {second.Display} ({second.Package.Id}), so scripts that can see both assemblies fail with CS0433 where they use {(distinct.Count == 1 ? "it" : "them")}.",
                    other: other.Package.Id, fix: "Rename the type in one of them, or move it into its own namespace.");
            }

            foreach (var (fullName, owners) in typeOwners)
            foreach (var type in owners)
            {
                var assembly = type.Assembly;
                if (type.Namespace.Length > 0 || !InFocus(assembly.Package) || assembly.Compile == AnalyzerCompile.No) continue;
                if (!(assembly.AutoReferenced || assembly.Consumers.Any(c => c != assembly))) continue;
                var file = type.Source is null ? null : Show(assembly.Package, type.Source.RelativePath);
                if (CoreTypeNames.Contains(type.Name) || OtherWellKnownTypeNames.Contains(type.Name))
                {
                    var core = CoreTypeNames.Contains(type.Name);
                    Add(IssueCodes.GlobalTypeShadows, core ? IssueSeverity.Error : IssueSeverity.Warning, assembly.Package.Id,
                        $"{type.Name} is a public type in the global namespace of {assembly.Display}, so scripts that can see it get this {type.Name} instead of Unity's or .NET's, and code that uses {type.Name} {(core ? "stops compiling" : "can stop compiling")}{(type.Name == "Debug" ? " (every Debug.Log call fails with CS0117)" : "")}.",
                        file, type.Line > 0 ? type.Line : null, fix: $"Put {type.Name} in a namespace.");
                }
                else if (namespacedBySimpleName.TryGetValue(type.Name, out var others)
                    && others.FirstOrDefault(o => o.Assembly != assembly && o.Assembly.Compile != AnalyzerCompile.No && o.Assembly.Consumers.Overlaps(assembly.Consumers)) is { } hidden)
                    Add(IssueCodes.GlobalTypeShadows, IssueSeverity.Warning, assembly.Package.Id,
                        $"{type.Name} is a public type in the global namespace of {assembly.Display}, so in scripts that see it, it hides {hidden.Namespace}.{hidden.Name} from {hidden.Assembly.Display} even where a using directive imports that namespace.",
                        file, type.Line > 0 ? type.Line : null, other: hidden.Assembly.Package.Id, fix: $"Put {type.Name} in a namespace.");
            }

            foreach (var (fullName, owners) in typeOwners)
            {
                if (!namespaceOwners.TryGetValue(fullName.Split('`')[0], out var spaces)) continue;
                foreach (var type in owners)
                {
                    var clash = spaces.FirstOrDefault(s => s != type.Assembly && (InFocus(s.Package) || InFocus(type.Assembly.Package)) && s.Compile != AnalyzerCompile.No);
                    if (clash is null) continue;
                    var reported = InFocus(type.Assembly.Package) ? type.Assembly : clash;
                    Add(IssueCodes.NamespaceTypeClash, IssueSeverity.Warning, reported.Package.Id,
                        $"{fullName.Split('`')[0]} is a type in {type.Assembly.Display} ({type.Assembly.Package.Id}) and a namespace in {clash.Display} ({clash.Package.Id}), so code that names it can fail to compile (CS0118 or CS0434).",
                        type.Source is null ? null : Show(type.Assembly.Package, type.Source.RelativePath), type.Line > 0 ? type.Line : null,
                        other: (reported == clash ? type.Assembly : clash).Package.Id, fix: "Rename the type or the namespace.");
                }
            }
        }

        private void DllRules()
        {
            var all = inventory.Packages.SelectMany(p => p.Plugins.Select(plugin => (Package: p, Plugin: plugin)));
            foreach (var group in all.GroupBy(x => x.Plugin.FileName, StringComparer.OrdinalIgnoreCase))
            {
                var copies = group.ToList();
                if (copies.Select(c => c.Package.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() < 2) continue;
                var focused = copies.Where(c => InFocus(c.Package)).ToList();
                if (focused.Count == 0) continue;
                var ordered = copies.OrderBy(c => Show(c.Plugin.FullPath), StringComparer.OrdinalIgnoreCase).ToList();
                var same = copies.Select(c => c.Plugin.Size).Distinct().Count() == 1 && copies.Select(c => Hash(c.Plugin.FullPath)).Distinct(StringComparer.Ordinal).Count() == 1;
                var places = List(ordered.Select(c => c.Package.Id).Distinct(StringComparer.OrdinalIgnoreCase), 4);
                var managed = copies.All(c => c.Plugin.IsManaged);
                var message = managed
                    ? same
                        ? $"{group.Key} is in {places}; Unity silently compiles against only one copy (here most likely {Show(ordered[0].Plugin.FullPath)})."
                        : $"{group.Key} is in {places} and the copies differ; Unity silently compiles against only one of them (here most likely {Show(ordered[0].Plugin.FullPath)}), so the other packages run against a version they weren't built for."
                    : $"The native plugin {group.Key} is in {places}; Unity loads only one native plugin with that name, and player builds can fail with \"Plugin used from several locations\" when both are enabled for the same platform.";
                var reported = focused[0];
                Add(IssueCodes.DllDuplicate, IssueSeverity.Warning, reported.Package.Id, message, Show(reported.Plugin.FullPath),
                    other: copies.Select(c => c.Package.Id).FirstOrDefault(p => !string.Equals(p, reported.Package.Id, StringComparison.OrdinalIgnoreCase)),
                    fix: "Ship the library as its own package that both depend on, or keep only one copy.");
            }
        }

        private static string Hash(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return path; }
        }

        private void GuidRules()
        {
            var index = new Dictionary<string, List<(InspectedPackage Package, string Asset)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var package in inventory.Packages)
            foreach (var (guid, assets) in package.Guids)
            {
                if (!index.TryGetValue(guid, out var list)) index[guid] = list = new();
                foreach (var asset in assets) list.Add((package, asset));
            }
            var pairs = new Dictionary<(string, string), List<(string Guid, string First, string Second)>>();
            foreach (var (guid, owners) in index)
            {
                if (owners.Count < 2 || !owners.Any(o => InFocus(o.Package))) continue;
                var first = owners.FirstOrDefault(o => InFocus(o.Package));
                foreach (var other in owners)
                {
                    if (other.Package == first.Package && other.Asset == first.Asset) continue;
                    var key = (first.Package.Id, other.Package.Id);
                    if (!pairs.TryGetValue(key, out var list)) pairs[key] = list = new();
                    list.Add((guid, Show(first.Package, first.Asset), Show(other.Package, other.Asset)));
                }
            }
            foreach (var ((package, other), list) in pairs)
            {
                var sample = list[0];
                Add(IssueCodes.GuidDuplicate, IssueSeverity.Error, package,
                    $"{Count(list.Count, "asset", "assets")} in {package} {(list.Count == 1 ? "shares its" : "share their")} .meta GUID with assets in {other} (for example {sample.First} and {sample.Second} both use {sample.Guid}), so Unity gives one of them a new GUID and references to it break.",
                    sample.First, other: string.Equals(package, other, StringComparison.OrdinalIgnoreCase) ? null : other,
                    fix: "Give the copied assets new GUIDs (delete their .meta files and let Unity recreate them), or remove the duplicate files.");
            }
        }

        private void EmbeddedRules()
        {
            foreach (var (id, first, second) in inventory.DuplicateEmbedded)
                if (focus.Contains(id) || options.Focus is null)
                    Add(IssueCodes.PackageDuplicate, IssueSeverity.Error, id, $"{first} and {second} both contain the package {id}, and Unity can only load one package with a given name.", first,
                        fix: "Delete or rename one of the folders.");
        }

        private void ReferenceRules()
        {
            foreach (var assembly in assemblies.Where(a => a.Kind == AnalyzerAssemblyKind.Asmdef && InFocus(a.Package)))
            {
                var asmdef = assembly.Asmdef!;
                var optional = asmdef.VersionDefines.Any(v => !present.Contains(v.Package) && !string.Equals(v.Package, "Unity", StringComparison.OrdinalIgnoreCase));
                foreach (var reference in asmdef.References)
                {
                    if (Resolve(reference) is not null) continue;
                    var shown = reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) ? $"the assembly with GUID {reference[5..]}" : reference;
                    Add(IssueCodes.AssemblyReferenceMissing, optional || assembly.Compile == AnalyzerCompile.No ? IssueSeverity.Info : IssueSeverity.Warning, assembly.Package.Id,
                        $"{assembly.Name} references {shown}, which isn't in this project; Unity drops the reference without a message, so any code that uses it fails to compile.",
                        Show(asmdef.FullPath), fix: $"Install the package that provides {shown}, or remove the reference.");
                }
                if (!asmdef.OverrideReferences) continue;
                foreach (var dll in asmdef.PrecompiledReferences)
                    if (!inventory.Packages.Any(p => p.Plugins.Any(x => string.Equals(x.FileName, dll, StringComparison.OrdinalIgnoreCase))) && !ProvidedByUnity(dll))
                        Add(IssueCodes.AssemblyReferenceMissing, optional ? IssueSeverity.Info : IssueSeverity.Warning, assembly.Package.Id,
                            $"{assembly.Name} lists {dll} in precompiledReferences, but no package in this project has that DLL; Unity drops the reference without a message, so any code that uses it fails to compile.",
                            Show(asmdef.FullPath), fix: $"Install the package that ships {dll}, or remove it from precompiledReferences.");
            }
        }

        private bool ProvidedByUnity(string dll)
        {
            if (inventory.EngineDlls is { } names) return names.Contains(dll);
            return dll.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || dll.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
                || dll.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) || dll is "mscorlib.dll" or "netstandard.dll";
        }

        private void NamespaceRules()
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in assemblies.Where(a => a.Kind != AnalyzerAssemblyKind.Plugin && a.Compile == AnalyzerCompile.Definite))
            foreach (var (source, package) in assembly.Sources)
            {
                if (!InFocus(package) || package.Origin == PackageOrigin.Project) continue;
                foreach (var directive in source.Info.Usings)
                {
                    if (directive.IsConditional || directive.IsStatic) continue;
                    var target = Clean(directive.Target);
                    if (target.Length == 0) continue;
                    var alias = directive.Alias is not null;
                    var candidates = Candidates(target, directive.Scope, alias).ToList();
                    if (candidates.Any(c => Sees(assembly, c))) continue;
                    if (alias && AliasResolves(assembly, target, directive.Scope)) continue;
                    var holders = candidates.SelectMany(c => namespaceOwners.GetValueOrDefault(c) ?? new List<AnalyzerAssembly>()).Where(o => o != assembly).Distinct().ToList();
                    var count = counts[package.Id] = counts.GetValueOrDefault(package.Id) + 1;
                    if (count > 25) continue;
                    var file = Show(package, source.RelativePath);
                    if (holders.Count > 0)
                    {
                        var holder = holders.OrderBy(h => h.Kind == AnalyzerAssemblyKind.Asmdef ? 0 : h.Kind == AnalyzerAssemblyKind.Plugin ? 1 : 2).First();
                        var fix = holder.Kind switch
                        {
                            AnalyzerAssemblyKind.Asmdef when assembly.Kind == AnalyzerAssemblyKind.Asmdef => $"Add {holder.Name} to the references in {Path.GetFileName(assembly.Asmdef!.FullPath)}.",
                            AnalyzerAssemblyKind.Asmdef => $"{holder.Name} isn't auto-referenced, so give these scripts an assembly definition that references it.",
                            AnalyzerAssemblyKind.Plugin when assembly.Kind == AnalyzerAssemblyKind.Asmdef => $"Add {holder.Display} to precompiledReferences in {Path.GetFileName(assembly.Asmdef!.FullPath)}.",
                            AnalyzerAssemblyKind.Plugin => $"{holder.Display} isn't auto-referenced, so give these scripts an assembly definition that lists it in precompiledReferences.",
                            _ => $"{holder.Name} can't be referenced by other assemblies; move that code into an assembly definition.",
                        };
                        Add(IssueCodes.NamespaceNotReferenced, IssueSeverity.Error, package.Id,
                            $"{source.RelativePath} uses namespace {target}, which is in {holder.Display} ({holder.Package.Id}), but {assembly.Display} doesn't reference it, so {assembly.Display} doesn't compile (CS0246).",
                            file, directive.Line, holder.Package.Id == package.Id ? null : holder.Package.Id, fix);
                    }
                    else if (inventory.EngineNamespaces is not null && UnityRoots.Any(r => target == r || target.StartsWith(r + ".", StringComparison.Ordinal)))
                        Add(IssueCodes.NamespaceMissing, IssueSeverity.Warning, package.Id,
                            $"{source.RelativePath} uses namespace {target}, which no Unity module or installed Unity package provides, so {assembly.Display} won't compile until the package that has it is installed.",
                            file, directive.Line, fix: $"Add the Unity package that provides {target} to the project.");
                    else
                        Add(IssueCodes.NamespaceMissing, IssueSeverity.Error, package.Id,
                            $"{source.RelativePath} uses namespace {target}, which nothing in this project provides, so {assembly.Display} doesn't compile (CS0246).",
                            file, directive.Line, fix: $"Install the package that provides {target}, or use the Basis version {package.Id} was written for.");
                }
            }
            foreach (var (package, count) in counts.Where(c => c.Value > 25))
                Add(IssueCodes.NamespaceMissing, IssueSeverity.Error, package, $"{Count(count - 25, "more using directive", "more using directives")} in {package} {(count - 25 == 1 ? "names a namespace" : "name namespaces")} this project doesn't provide.");
        }

        private bool AliasResolves(AnalyzerAssembly assembly, string target, string scope)
        {
            var parts = target.Split('.');
            for (var length = parts.Length; length >= 1; length--)
            {
                var prefix = string.Join('.', parts.Take(length));
                foreach (var candidate in Candidates(prefix, scope, false))
                {
                    if (Sees(assembly, candidate)) return true;
                    if (typeOwners.TryGetValue(candidate, out var types) && types.Any(t => t.Assembly == assembly || assembly.Visible.Contains(t.Assembly))) return true;
                    if (assembly.Sources.Any(s => s.Source.Info.Types.Any(t => t.FullName == candidate))) return true;
                }
            }
            return false;
        }

        private static string Clean(string target)
        {
            var text = target.Trim();
            if (text.StartsWith("global::", StringComparison.Ordinal)) text = text[8..];
            var generic = text.IndexOf('<');
            if (generic >= 0) text = text[..generic];
            return text.Trim('.', ' ');
        }

        private static IEnumerable<string> Candidates(string target, string scope, bool alias)
        {
            var names = alias && target.Contains('.') ? new[] { target, target[..target.LastIndexOf('.')] } : new[] { target };
            foreach (var name in names)
            {
                var prefix = scope;
                while (prefix.Length > 0)
                {
                    yield return prefix + "." + name;
                    var dot = prefix.LastIndexOf('.');
                    prefix = dot < 0 ? "" : prefix[..dot];
                }
                yield return name;
            }
        }

        private bool Sees(AnalyzerAssembly assembly, string ns)
        {
            if (assembly.Namespaces.Contains(ns)) return true;
            foreach (var visible in assembly.Visible)
                if (visible.Namespaces.Contains(ns)) return true;
            if (AlwaysKnownRoots.Any(r => ns == r || ns.StartsWith(r + ".", StringComparison.Ordinal))) return true;
            if (assembly.NoEngine) return false;
            return inventory.EngineNamespaces is { } engine ? engine.Contains(ns) : FallbackRoots.Any(r => ns == r || ns.StartsWith(r + ".", StringComparison.Ordinal));
        }
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9_-]*(\.[a-z0-9_-]+)+$")]
    private static partial Regex PackageName();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex StrictVersion();

    [GeneratedRegex(@"^\d{4}\.\d+$")]
    private static partial Regex UnityField();

    [GeneratedRegex(@"^\d+[abfpx]\d+$")]
    private static partial Regex UnityReleaseField();
}
