using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public enum ScaffoldLicense { Mit, Apache2, None }

public enum PackageIdProblem { None, Missing, Characters, NoDot, Reserved, TooLong }

public sealed record PackageScaffoldRequest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string? AuthorUrl { get; init; }
    public ScaffoldLicense License { get; init; } = ScaffoldLicense.Mit;
    public string? Unity { get; init; }
    public string? BasisFrom { get; init; }
    public IReadOnlyList<string> References { get; init; } = Array.Empty<string>();
    public bool Runtime { get; init; } = true;
    public bool Editor { get; init; } = true;
    public bool Server { get; init; }
    public bool Samples { get; init; }
    public int Year { get; init; } = DateTime.UtcNow.Year;
}

public sealed record PackageScaffoldResult(bool Ok, string Message, string? Folder = null, string? ManifestLine = null)
{
    public static PackageScaffoldResult Fail(string message) => new(false, message);
}

public sealed record BasisAssembly(string Name, string PackageId, bool EditorOnly);

public sealed class PackageScaffoldService
{
    public const string InitialVersion = "0.1.0";
    public static readonly IReadOnlyList<string> DefaultReferences = new[] { "Basis Framework", "BasisSDK", "BasisCommon" };

    private static readonly Regex IdCharacters = new(@"^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DomainPrefixes = new(StringComparer.OrdinalIgnoreCase) { "com", "org", "net", "io", "dev", "app", "gg", "co", "me", "xyz", "tv", "uk", "au", "de" };
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly GitService _git;
    private readonly UnityProjectService _projects;
    private readonly MountService _mounts;

    public PackageScaffoldService(GitService git, UnityProjectService projects, MountService mounts)
    {
        _git = git;
        _projects = projects;
        _mounts = mounts;
    }

    public static PackageIdProblem ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return PackageIdProblem.Missing;
        if (id.Length > 214) return PackageIdProblem.TooLong;
        if (!IdCharacters.IsMatch(id) || id.EndsWith('.') || id.Contains("..", StringComparison.Ordinal) || !BasisDevStore.IsValidId(id)) return PackageIdProblem.Characters;
        if (!id.Contains('.')) return PackageIdProblem.NoDot;
        if (id.StartsWith("com.unity.", StringComparison.Ordinal)) return PackageIdProblem.Reserved;
        return PackageIdProblem.None;
    }

    public static string DescribeIdProblem(PackageIdProblem problem, string? id) => problem switch
    {
        PackageIdProblem.Missing => "A package needs an id, such as com.yourname.yourpackage.",
        PackageIdProblem.TooLong => "The package id is longer than 214 characters.",
        PackageIdProblem.Characters => $"\"{id}\" isn't a valid package id: use lowercase letters, digits, dots, dashes and underscores, starting with a letter or digit.",
        PackageIdProblem.NoDot => $"\"{id}\" needs at least one dot, such as com.yourname.{id}.",
        PackageIdProblem.Reserved => "Package ids starting with com.unity. belong to Unity.",
        _ => "",
    };

    public static string SuggestId(string? owner, string? displayName)
    {
        var name = Slug(displayName);
        var who = Slug(owner);
        return $"com.{(who.Length == 0 ? "mystudio" : who)}.{(name.Length == 0 ? "package" : name)}";
    }

    public static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder();
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        return builder.ToString().Trim('-');
    }

    public static string AssemblyName(string id)
    {
        var segments = id.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 1 && DomainPrefixes.Contains(segments[0])) segments.RemoveAt(0);
        var parts = segments.Select(Pascal).Where(p => p.Length > 0).ToList();
        return parts.Count == 0 ? "Package" : string.Join('.', parts);
    }

    private static string Pascal(string segment)
    {
        var builder = new StringBuilder();
        foreach (var word in segment.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            builder.Append(char.ToUpperInvariant(word[0])).Append(word[1..]);
        var result = builder.ToString();
        return result.Length > 0 && char.IsDigit(result[0]) ? "Pkg" + result : result;
    }

    public static string? UnityMajorMinor(string? unityVersion) =>
        UnityVersion.TryParse(unityVersion, out var version) ? $"{version.Major}.{version.Minor}" : null;

    public static IReadOnlyList<BasisAssembly> FindBasisAssemblies(string unityProjectPath)
    {
        var found = new Dictionary<string, BasisAssembly>(StringComparer.Ordinal);
        foreach (var package in UnityPackageMap.List(unityProjectPath, includeAssets: false))
        {
            if (!package.Id.StartsWith("com.basis.", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var asmdef in AssemblyDefinitions(package.Root))
            {
                var assembly = ReadAssembly(asmdef, package.Id);
                if (assembly is not null) found.TryAdd(assembly.Name, assembly);
            }
        }
        return found.Values.OrderBy(a => a.EditorOnly).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> AssemblyDefinitions(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            string[] files, folders;
            try
            {
                files = Directory.GetFiles(folder, "*.asmdef");
                folders = Directory.GetDirectories(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var file in files) yield return file;
            foreach (var child in folders)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name.EndsWith('~') || name.Equals("Tests", StringComparison.OrdinalIgnoreCase)) continue;
                pending.Push(child);
            }
        }
    }

    private static BasisAssembly? ReadAssembly(string asmdef, string packageId)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(asmdef), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var nameElement) || nameElement.GetString() is not { Length: > 0 } name) return null;
            if (Strings(root, "defineConstraints").Any(d => d.Contains("UNITY_INCLUDE_TESTS", StringComparison.Ordinal))) return null;
            if (Strings(root, "optionalUnityReferences").Contains("TestAssemblies")) return null;
            var platforms = Strings(root, "includePlatforms").ToList();
            return new BasisAssembly(name, packageId, platforms.Count == 1 && platforms[0] == "Editor");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> Strings(JsonElement root, string property) =>
        root.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "")
            : Enumerable.Empty<string>();

    public static IReadOnlyDictionary<string, string> CreateFiles(PackageScaffoldRequest request)
    {
        var assembly = AssemblyName(request.Id);
        var shortName = assembly.Split('.')[^1];
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["package.json"] = PackageJson(request),
            ["README.md"] = Readme(request),
            ["CHANGELOG.md"] = "# Changelog\n\nAll notable changes to this package are listed here.\n",
        };
        if (LicenseText(request.License, request.Author, request.Year) is { } license) files["LICENSE"] = license;
        if (request.Runtime)
        {
            files[$"Runtime/{assembly}.asmdef"] = AssemblyDefinition(assembly, request.References, editor: false);
            files[$"Runtime/{shortName}Behaviour.cs"] = $"using UnityEngine;\n\nnamespace {assembly}\n{{\n    public class {shortName}Behaviour : MonoBehaviour\n    {{\n    }}\n}}\n";
        }
        if (request.Editor)
            files[$"Editor/{assembly}.Editor.asmdef"] = AssemblyDefinition(assembly + ".Editor", request.Runtime ? new[] { assembly }.Concat(request.References).ToList() : request.References, editor: true);
        if (request.Server)
            files[$"Server~/BasisNetworkServer/{shortName}ServerPackage.cs"] =
                $"using Basis.Network.Core;\n\n[assembly: BasisNetworkPackage(typeof({assembly}.Server.{shortName}ServerPackage))]\n\nnamespace {assembly}.Server\n{{\n    public sealed class {shortName}ServerPackage : IBasisNetworkPackage\n    {{\n        public void Initialize()\n        {{\n        }}\n    }}\n}}\n";
        if (request.Samples)
            files["Samples~/Example/README.md"] = $"# {request.DisplayName} example\n\nImport this sample from Unity's Package Manager window to see {request.DisplayName} in use.\n";
        return files;
    }

    public static string PackageJson(PackageScaffoldRequest request)
    {
        var json = new JsonObject
        {
            ["name"] = request.Id,
            ["displayName"] = request.DisplayName.Trim(),
            ["version"] = InitialVersion,
            ["description"] = request.Description.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(request.Unity)) json["unity"] = request.Unity.Trim();
        if (!string.IsNullOrWhiteSpace(request.Author))
        {
            var author = new JsonObject { ["name"] = request.Author.Trim() };
            if (!string.IsNullOrWhiteSpace(request.AuthorUrl)) author["url"] = request.AuthorUrl.Trim();
            json["author"] = author;
        }
        if (SpdxOf(request.License) is { } spdx) json["license"] = spdx;
        if (PackageCompatibility.ParseBasisDate(request.BasisFrom) is { } from) json["basis"] = new JsonObject { ["from"] = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        if (request.Samples)
            json["samples"] = new JsonArray(new JsonObject { ["displayName"] = "Example", ["description"] = $"Shows {request.DisplayName.Trim()} in use.", ["path"] = "Samples~/Example" });
        return json.ToJsonString(Json) + "\n";
    }

    public static string? SpdxOf(ScaffoldLicense license) => license switch
    {
        ScaffoldLicense.Mit => "MIT",
        ScaffoldLicense.Apache2 => "Apache-2.0",
        _ => null,
    };

    private static string AssemblyDefinition(string name, IReadOnlyList<string> references, bool editor)
    {
        var json = new JsonObject
        {
            ["name"] = name,
            ["rootNamespace"] = name,
            ["references"] = new JsonArray(references.Distinct(StringComparer.Ordinal).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["includePlatforms"] = editor ? new JsonArray(JsonValue.Create("Editor")) : new JsonArray(),
            ["excludePlatforms"] = new JsonArray(),
            ["allowUnsafeCode"] = false,
            ["overrideReferences"] = false,
            ["precompiledReferences"] = new JsonArray(),
            ["autoReferenced"] = true,
            ["defineConstraints"] = new JsonArray(),
            ["versionDefines"] = new JsonArray(),
            ["noEngineReferences"] = false,
        };
        return json.ToJsonString(Json) + "\n";
    }

    private static string Readme(PackageScaffoldRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("# ").Append(request.DisplayName.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(request.Description)) builder.Append(request.Description.Trim()).Append("\n\n");
        builder.Append("## Install\n\n");
        builder.Append("In the Basis Package Manager, open Packages, choose Add from Git URL and paste this repository's URL. ");
        builder.Append("In Unity, open Window > Package Manager, choose + > Add package from git URL and paste the same URL.\n\n");
        builder.Append("## Contents\n\n");
        if (request.Runtime) builder.Append("- `Runtime/`: code that runs in the app.\n");
        if (request.Editor) builder.Append("- `Editor/`: tools for the Unity editor.\n");
        if (request.Server) builder.Append("- `Server~/`: code the Basis Server builds in, see Basis Server/Packages/README.md in Basis.\n");
        if (request.Samples) builder.Append("- `Samples~/`: examples to import from Unity's Package Manager window.\n");
        builder.Append("- `CHANGELOG.md`: what changed in each version.\n");
        return builder.ToString();
    }

    public static string? LicenseText(ScaffoldLicense license, string author, int year)
    {
        var holder = string.IsNullOrWhiteSpace(author) ? "the authors" : author.Trim();
        return license switch
        {
            ScaffoldLicense.Mit => $"MIT License\n\nCopyright (c) {year} {holder}\n\n"
                + "Permission is hereby granted, free of charge, to any person obtaining a copy\n"
                + "of this software and associated documentation files (the \"Software\"), to deal\n"
                + "in the Software without restriction, including without limitation the rights\n"
                + "to use, copy, modify, merge, publish, distribute, sublicense, and/or sell\n"
                + "copies of the Software, and to permit persons to whom the Software is\n"
                + "furnished to do so, subject to the following conditions:\n\n"
                + "The above copyright notice and this permission notice shall be included in all\n"
                + "copies or substantial portions of the Software.\n\n"
                + "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR\n"
                + "IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,\n"
                + "FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE\n"
                + "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER\n"
                + "LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,\n"
                + "OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE\n"
                + "SOFTWARE.\n",
            ScaffoldLicense.Apache2 => $"Copyright {year} {holder}\n\n"
                + "Licensed under the Apache License, Version 2.0 (the \"License\");\n"
                + "you may not use this file except in compliance with the License.\n"
                + "You may obtain a copy of the License at\n\n"
                + "    http://www.apache.org/licenses/LICENSE-2.0\n\n"
                + "Unless required by applicable law or agreed to in writing, software\n"
                + "distributed under the License is distributed on an \"AS IS\" BASIS,\n"
                + "WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.\n"
                + "See the License for the specific language governing permissions and\n"
                + "limitations under the License.\n",
            _ => null,
        };
    }

    public async Task<DateOnly?> ResolveBasisDateAsync(BasisUpdateService updates, BasisInstall install, CancellationToken ct = default)
    {
        if (!install.IsGitRepo || !_git.IsAvailable) return null;
        try
        {
            var basis = await updates.ResolveBasisBaseAsync(install.RepoRoot, ct).ConfigureAwait(false);
            if (basis is null) return null;
            var date = await _git.GetCommitDateAsync(install.RepoRoot, basis.Sha, ct).ConfigureAwait(false);
            return date is { } when ? DateOnly.FromDateTime(when.UtcDateTime) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Working out the Basis version of {install.RepoRoot}", ex);
            return null;
        }
    }

    public string? FindConflict(BasisInstall install, string id)
    {
        var folder = BasisDevStore.CloneFolder(install.UnityProjectPath, id);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) return $"{BasisDevStore.FolderName}/{id} already exists.";
        if (Directory.Exists(Path.Combine(install.UnityProjectPath, "Packages", id))) return $"Packages/{id} already exists.";
        if (UnityPackageMap.ReadDependencies(Path.Combine(install.UnityProjectPath, "Packages", "manifest.json")).ContainsKey(id)) return $"manifest.json already has {id}.";
        return _projects.ListEmbeddedPackages(install.UnityProjectPath).Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
            ? $"The project already has a package called {id}."
            : null;
    }

    public async Task<PackageScaffoldResult> CreateAsync(BasisInstall install, PackageScaffoldRequest request, CancellationToken ct = default)
    {
        var problem = ValidateId(request.Id);
        if (problem != PackageIdProblem.None) return PackageScaffoldResult.Fail(DescribeIdProblem(problem, request.Id));
        if (string.IsNullOrWhiteSpace(request.DisplayName)) return PackageScaffoldResult.Fail("A package needs a display name.");
        if (FindConflict(install, request.Id) is { } conflict) return PackageScaffoldResult.Fail(conflict);

        var folder = BasisDevStore.CloneFolder(install.UnityProjectPath, request.Id);
        try
        {
            foreach (var (relative, content) in CreateFiles(request))
            {
                var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Writing the files of {request.Id}", ex);
            TryDelete(folder);
            return PackageScaffoldResult.Fail($"Couldn't write {folder}: {ex.Message}");
        }

        var gitNote = "";
        if (_git.IsAvailable)
        {
            var init = await _git.InitAsync(folder, "main", ct).ConfigureAwait(false);
            var commit = init.Ok ? await _git.CommitAllAsync(folder, $"Create {request.Id}", ct).ConfigureAwait(false) : init;
            if (!commit.Ok) gitNote = $" Git couldn't record the first commit: {commit.Output}";
        }
        else gitNote = " Git wasn't found, so the folder isn't a git repository yet.";

        var line = BasisDevStore.ManifestLine(install.UnityProjectPath, folder);
        try
        {
            var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
            info.Manifest.Dependencies[request.Id] = line;
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
            install.Manifest = info.Manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            DiagnosticLog.Write($"Adding {request.Id} to manifest.json", ex);
            return new PackageScaffoldResult(false, $"Created {BasisDevStore.FolderName}/{request.Id}, but couldn't add it to manifest.json: {ex.Message}", folder, line);
        }
        if (MountService.IsWorkingClone(folder)) await _mounts.RecordAsync(install, request.Id, folder, "", line).ConfigureAwait(false);
        return new PackageScaffoldResult(true, $"Created {request.Id} in {BasisDevStore.FolderName}/{request.Id}; manifest.json loads it from {line}.{gitNote}", folder, line);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) FileRetry.DeleteDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Cleaning up {folder}", ex); }
    }
}
