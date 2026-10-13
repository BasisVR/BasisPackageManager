using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class UnityLogSummary
{
    public List<CompileDiagnostic> Diagnostics { get; } = new();
    public List<string> FailedAssemblies { get; } = new();
    public List<string> ApiUpdatedFiles { get; } = new();
    public bool CompilerErrors { get; set; }
    public bool BatchAborted { get; set; }
    public string? FatalError { get; set; }
    public bool ProjectAlreadyOpen { get; set; }
    public bool CompileRequested { get; set; }
    public bool PackagesResolving { get; set; }
    public bool PackageResolutionFailed { get; set; }
    public int Builds { get; set; }
    public bool? LastBuildSucceeded { get; set; }
    public bool BuildStartedAfterLastResult { get; set; }
    public bool LicenseResolved { get; set; }
    public string? LicenseProblem { get; set; }
    public int? ExitCode { get; set; }
    public bool ExitedCleanly { get; set; }

    public bool LicenseFailed => LicenseProblem is not null && !LicenseResolved;
    public bool HasErrors => CompilerErrors || LastBuildSucceeded == false || PackageResolutionFailed || Diagnostics.Any(d => d.Severity == IssueSeverity.Error);
    public int ErrorCount => Diagnostics.Count(d => d.Severity == IssueSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == IssueSeverity.Warning);
}

public sealed record TundraNodeResult(string Annotation, string? Assembly, int ExitCode, IReadOnlyList<CompileDiagnostic> Diagnostics);

public sealed class TundraLogSummary
{
    public bool Found { get; init; }
    public DateTime? Modified { get; init; }
    public List<TundraNodeResult> Nodes { get; } = new();
    public List<CompileDiagnostic> Diagnostics { get; } = new();
    public List<string> FailedAssemblies { get; } = new();

    public bool Failed => Nodes.Any(n => n.ExitCode != 0);
}

public sealed partial class UnityLogParser
{
    public const string ApiUpdatedCode = "api-updated";

    private static readonly string[] LicenseResolvedMarkers = { "[Licensing::Module] License group:", "Successfully resolved entitlement details", "Successfully updated license" };
    private static readonly string[] LicenseProblemMarkers =
    {
        "No valid Unity Editor license found", "No valid Unity license", "License is not active", "Unable to find a valid license", "No ULF license found",
        "is not entitled", "Failed to activate the license", "Failed to update license", "You need a valid license",
    };

    private readonly string? _projectPath;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly StringBuilder _pending = new();
    private string? _assembly;
    private bool _inResolution;
    private string? _resolutionPackage;
    private int _lastResolution = -1;
    private bool _inApiUpdater;
    private bool _expectExitCode;
    private bool _expectFatal;

    public UnityLogParser(string? unityProjectPath = null, string? assembly = null)
    {
        _projectPath = string.IsNullOrWhiteSpace(unityProjectPath) ? null : Path.GetFullPath(unityProjectPath);
        _assembly = assembly;
    }

    public UnityLogSummary Summary { get; } = new();

    public static UnityLogSummary Parse(string text, string? unityProjectPath = null)
    {
        var parser = new UnityLogParser(unityProjectPath);
        parser.Feed(text);
        parser.Complete();
        return parser.Summary;
    }

    public static UnityLogSummary Parse(IEnumerable<string> lines, string? unityProjectPath = null)
    {
        var parser = new UnityLogParser(unityProjectPath);
        foreach (var line in lines) parser.AddLine(line);
        parser.Complete();
        return parser.Summary;
    }

    public void Feed(string chunk)
    {
        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                AddLine(_pending.ToString());
                _pending.Clear();
            }
            else if (c != '\r') _pending.Append(c);
        }
    }

    public void FeedFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[1 << 16];
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) Feed(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {path}", ex); }
    }

    public void Complete()
    {
        if (_pending.Length > 0)
        {
            AddLine(_pending.ToString());
            _pending.Clear();
        }
        _inResolution = false;
        _inApiUpdater = false;
    }

    public void AddLine(string line)
    {
        if (_expectFatal && line.Trim().Length > 0)
        {
            Summary.FatalError ??= line.Trim();
            _expectFatal = false;
        }
        if (_inResolution && ResolutionLine(line)) return;
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return;

        if (trimmed.StartsWith("An error occurred while resolving packages", StringComparison.Ordinal))
        {
            _inResolution = true;
            _resolutionPackage = null;
            _lastResolution = -1;
            Summary.PackageResolutionFailed = true;
            return;
        }
        if (_inApiUpdater)
        {
            if (!trimmed.StartsWith('[') && !trimmed.StartsWith('*') && trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && (trimmed.Contains('/') || trimmed.Contains('\\')))
            {
                var file = NormalizePath(trimmed);
                if (!Summary.ApiUpdatedFiles.Contains(file)) Summary.ApiUpdatedFiles.Add(file);
                Add(new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Info, ApiUpdatedCode, "Unity's API Updater rewrote this file to replace obsolete Unity APIs.") { File = file });
                return;
            }
            _inApiUpdater = false;
        }
        if (trimmed.StartsWith("[API Updater] Updated Files:", StringComparison.Ordinal))
        {
            _inApiUpdater = true;
            return;
        }
        if (_expectExitCode)
        {
            _expectExitCode = false;
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var nodeExit))
            {
                if (nodeExit != 0) AddFailedAssembly(_assembly);
                return;
            }
        }
        if (trimmed == "##### ExitCode")
        {
            _expectExitCode = true;
            return;
        }

        var compiler = CompilerRegex().Match(line);
        if (compiler.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.Compiler, Severity(compiler.Groups["sev"].Value), compiler.Groups["code"].Value, compiler.Groups["msg"].Value.Trim())
            {
                File = NormalizePath(compiler.Groups["file"].Value),
                Line = int.Parse(compiler.Groups["line"].Value, CultureInfo.InvariantCulture),
                Column = int.Parse(compiler.Groups["col"].Value, CultureInfo.InvariantCulture),
                Assembly = _assembly,
            });
            return;
        }
        var tool = ToolDiagnosticRegex().Match(line);
        if (tool.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.Compiler, Severity(tool.Groups["sev"].Value), tool.Groups["code"].Value, tool.Groups["msg"].Value.Trim()) { Assembly = _assembly });
            return;
        }

        var node = BeeNodeRegex().Match(line);
        if (node.Success)
        {
            _assembly = node.Groups["asm"].Value;
            if (trimmed.StartsWith("## Script Compilation Error for:", StringComparison.Ordinal)) AddFailedAssembly(_assembly);
            return;
        }

        if (trimmed.StartsWith("*** Tundra build success", StringComparison.Ordinal) || trimmed.StartsWith("*** Tundra build failed", StringComparison.Ordinal))
        {
            Summary.Builds++;
            Summary.LastBuildSucceeded = trimmed.StartsWith("*** Tundra build success", StringComparison.Ordinal);
            Summary.BuildStartedAfterLastResult = false;
            return;
        }
        if (trimmed.StartsWith("[ScriptCompilation] Requested script compilation", StringComparison.Ordinal))
        {
            Summary.CompileRequested = true;
            BuildStarted();
            return;
        }
        if (trimmed.StartsWith("Starting: ", StringComparison.Ordinal) && trimmed.Contains("bee_backend", StringComparison.OrdinalIgnoreCase))
        {
            BuildStarted();
            return;
        }
        if (trimmed.StartsWith("[Package Manager]", StringComparison.Ordinal) && trimmed.Contains("resolving packages", StringComparison.OrdinalIgnoreCase))
        {
            Summary.PackagesResolving = !trimmed.Contains("Done resolving", StringComparison.OrdinalIgnoreCase);
            return;
        }
        if (trimmed == "Scripts have compiler errors.")
        {
            Summary.CompilerErrors = true;
            return;
        }
        if (trimmed.StartsWith("Aborting batchmode due to", StringComparison.Ordinal))
        {
            Summary.BatchAborted = true;
            _expectFatal = trimmed.Contains("fatal error", StringComparison.Ordinal);
            return;
        }
        if (trimmed.Contains("another Unity instance is running with this project open", StringComparison.Ordinal) || trimmed.Contains("Multiple Unity instances cannot open the same project", StringComparison.Ordinal))
        {
            Summary.ProjectAlreadyOpen = true;
            return;
        }
        var exit = ExitCodeRegex().Match(trimmed);
        if (exit.Success)
        {
            Summary.ExitCode = int.Parse(exit.Groups["code"].Value, CultureInfo.InvariantCulture);
            return;
        }
        if (trimmed.StartsWith("Exiting batchmode successfully now", StringComparison.Ordinal))
        {
            Summary.ExitedCleanly = true;
            return;
        }

        var duplicateAssembly = DuplicateAsmdefRegex().Match(trimmed);
        if (duplicateAssembly.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.AssemblyDefinition, IssueSeverity.Error, IssueCodes.AssemblyDuplicate, $"Assembly with name '{duplicateAssembly.Groups["name"].Value}' already exists")
            {
                File = NormalizePath(duplicateAssembly.Groups["path"].Value),
                Assembly = duplicateAssembly.Groups["name"].Value,
            });
            return;
        }
        var missingReference = MissingReferenceRegex().Match(trimmed);
        if (missingReference.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.AssemblyDefinition, IssueSeverity.Warning, IssueCodes.AssemblyReferenceMissing, $"References an assembly that isn't in the project: {missingReference.Groups["ref"].Value}")
            {
                File = NormalizePath(missingReference.Groups["path"].Value),
            });
            return;
        }
        var skipped = SkippedScriptRegex().Match(trimmed);
        if (skipped.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Info, IssueCodes.ScriptsOutsideAssembly, "Unity doesn't compile this script: it's outside Assets and no assembly definition includes it.")
            {
                File = NormalizePath(skipped.Groups["path"].Value),
            });
            return;
        }
        var duplicateDll = DuplicateDllRegex().Match(line);
        if (duplicateDll.Success)
        {
            var name = duplicateDll.Groups["name"].Value;
            var used = NormalizePath(duplicateDll.Groups["used"].Value);
            var ignored = NormalizePath(duplicateDll.Groups["ignored"].Value);
            Add(new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Warning, IssueCodes.DllDuplicate, $"Two versions of {name} are in the project. Unity uses {used} and ignores this one.") { File = ignored });
            return;
        }
        var multiple = MultiplePrecompiledRegex().Match(trimmed);
        if (multiple.Success)
        {
            Add(new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Error, IssueCodes.DllDuplicate, $"More than one {multiple.Groups["name"].Value} is included for this platform; Unity allows only one.")
            {
                File = multiple.Groups["path"].Success ? NormalizePath(multiple.Groups["path"].Value) : null,
            });
            return;
        }

        if (trimmed.StartsWith("[Licensing::", StringComparison.Ordinal) || trimmed.Contains("license", StringComparison.OrdinalIgnoreCase))
        {
            if (LicenseResolvedMarkers.Any(m => trimmed.Contains(m, StringComparison.Ordinal))) Summary.LicenseResolved = true;
            else if (Summary.LicenseProblem is null && LicenseProblemMarkers.Any(m => trimmed.Contains(m, StringComparison.OrdinalIgnoreCase))) Summary.LicenseProblem = trimmed;
        }
    }

    private bool ResolutionLine(string line)
    {
        if (line.Trim().Length == 0) return true;
        if (!char.IsWhiteSpace(line[0]))
        {
            _inResolution = false;
            return false;
        }
        var package = ResolutionPackageRegex().Match(line);
        if (package.Success)
        {
            _resolutionPackage = package.Groups["id"].Value;
            _lastResolution = -1;
            return true;
        }
        if (line.TrimStart().StartsWith("Project has invalid dependencies", StringComparison.Ordinal))
        {
            _resolutionPackage = null;
            _lastResolution = -1;
            return true;
        }
        var dependency = ResolutionDependencyRegex().Match(line);
        if (dependency.Success)
        {
            var id = dependency.Groups["id"].Value;
            var label = dependency.Groups["kind"].Success ? $"{id} ({dependency.Groups["kind"].Value})" : id;
            var diagnostic = new CompileDiagnostic(CompileDiagnosticKind.PackageResolution, IssueSeverity.Error, IssueCodes.PackageResolution, $"{label}: {dependency.Groups["reason"].Value.Trim()}")
            {
                Package = _resolutionPackage ?? id,
            };
            if (Add(diagnostic)) _lastResolution = Summary.Diagnostics.Count - 1;
            else _lastResolution = -1;
            return true;
        }
        if (_lastResolution >= 0 && _lastResolution < Summary.Diagnostics.Count)
        {
            var previous = Summary.Diagnostics[_lastResolution];
            Summary.Diagnostics[_lastResolution] = previous with { Message = previous.Message + " " + line.Trim() };
        }
        return true;
    }

    private void BuildStarted()
    {
        if (Summary.Builds > 0 && !Summary.BuildStartedAfterLastResult)
        {
            foreach (var stale in Summary.Diagnostics.Where(d => d.Kind == CompileDiagnosticKind.Compiler || (d.Kind == CompileDiagnosticKind.Other && d.Code == IssueCodes.CompileError)).ToList())
            {
                Summary.Diagnostics.Remove(stale);
                _seen.Remove(Key(stale));
            }
            Summary.FailedAssemblies.Clear();
            Summary.CompilerErrors = false;
            _lastResolution = -1;
        }
        Summary.BuildStartedAfterLastResult = true;
    }

    private bool Add(CompileDiagnostic diagnostic)
    {
        if (!_seen.Add(Key(diagnostic))) return false;
        Summary.Diagnostics.Add(diagnostic);
        return true;
    }

    private static string Key(CompileDiagnostic d) => string.Join('|', d.Kind, d.Severity, d.File, d.Line, d.Column, d.Code, d.Message, d.Package);

    private void AddFailedAssembly(string? assembly)
    {
        if (!string.IsNullOrEmpty(assembly) && !Summary.FailedAssemblies.Contains(assembly, StringComparer.Ordinal)) Summary.FailedAssemblies.Add(assembly);
    }

    private string NormalizePath(string path)
    {
        var raw = path.Trim().Trim('"');
        if (_projectPath is not null && IsRooted(raw))
        {
            try
            {
                var relative = Path.GetRelativePath(_projectPath, raw);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative)) raw = relative;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        raw = raw.Replace('\\', '/');
        return raw.StartsWith("./", StringComparison.Ordinal) ? raw[2..] : raw;
    }

    private static bool IsRooted(string path) =>
        Path.IsPathRooted(path) || (path.Length > 2 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));

    private static IssueSeverity Severity(string text) => text.ToLowerInvariant() switch
    {
        "error" => IssueSeverity.Error,
        "warning" => IssueSeverity.Warning,
        _ => IssueSeverity.Info,
    };

    public static string? DescribeProgress(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith("[Package Manager]", StringComparison.Ordinal) && (trimmed.Contains("resolving packages", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("Registering", StringComparison.OrdinalIgnoreCase)))
            return "Resolving packages…";
        if (trimmed.StartsWith("Importing '", StringComparison.Ordinal) || trimmed.StartsWith("Start importing", StringComparison.Ordinal) || trimmed.StartsWith("Asset Pipeline Refresh", StringComparison.Ordinal))
            return "Importing assets…";
        if (trimmed.StartsWith("[ScriptCompilation] Requested script compilation", StringComparison.Ordinal) || (trimmed.StartsWith("Starting: ", StringComparison.Ordinal) && trimmed.Contains("bee_backend", StringComparison.OrdinalIgnoreCase)))
            return "Compiling scripts…";
        if (trimmed.StartsWith("*** Tundra build success", StringComparison.Ordinal)) return "Scripts compiled.";
        if (trimmed.StartsWith("*** Tundra build failed", StringComparison.Ordinal)) return "Scripts failed to compile.";
        if (trimmed.StartsWith("Reloading assemblies", StringComparison.Ordinal) || trimmed.StartsWith("Begin MonoManager ReloadAssembly", StringComparison.Ordinal))
            return "Loading the compiled scripts…";
        if (trimmed.StartsWith("Exiting batchmode successfully", StringComparison.Ordinal)) return "Unity finished.";
        return null;
    }

    public static TundraLogSummary ParseTundraLog(string path, string? unityProjectPath = null, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new TundraLogSummary { Found = false };
        var summary = new TundraLogSummary { Found = true, Modified = File.GetLastWriteTimeUtc(path) };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } line)
            {
                ct.ThrowIfCancellationRequested();
                if (!line.Contains("\"noderesult\"", StringComparison.Ordinal)) continue;
                if (ReadNode(line, unityProjectPath) is not { } node) continue;
                if (node.ExitCode == 0 && node.Diagnostics.Count == 0) continue;
                summary.Nodes.Add(node);
                if (node.ExitCode != 0 && node.Assembly is { } failed && !summary.FailedAssemblies.Contains(failed, StringComparer.Ordinal)) summary.FailedAssemblies.Add(failed);
                foreach (var diagnostic in node.Diagnostics)
                    if (seen.Add(string.Join('|', diagnostic.Kind, diagnostic.Severity, diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code, diagnostic.Message)))
                        summary.Diagnostics.Add(diagnostic);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Reading {path}", ex); }
        return summary;
    }

    private static TundraNodeResult? ReadNode(string line, string? unityProjectPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("msg", out var msg) || msg.GetString() != "noderesult") return null;
            var annotation = root.TryGetProperty("annotation", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "" : "";
            var exitCode = root.TryGetProperty("exitcode", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var code) ? code : 0;
            var stdout = root.TryGetProperty("stdout", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
            var node = BeeNodeRegex().Match(annotation);
            var assembly = node.Success ? node.Groups["asm"].Value : null;
            var parser = new UnityLogParser(unityProjectPath, assembly);
            parser.Feed(stdout);
            parser.Complete();
            var diagnostics = parser.Summary.Diagnostics.ToList();
            if (exitCode != 0 && !diagnostics.Any(d => d.Severity == IssueSeverity.Error))
            {
                var first = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                diagnostics.Add(new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Error, IssueCodes.CompileError,
                    first is null ? $"Unity's build step failed: {annotation}" : $"Unity's build step failed: {annotation}: {first}") { Assembly = assembly });
            }
            return new TundraNodeResult(annotation, assembly, exitCode, diagnostics);
        }
        catch (JsonException) { return null; }
    }

    public static string Tail(string text, int lines)
    {
        var all = text.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        return string.Join('\n', all.Skip(Math.Max(0, all.Count - lines)));
    }

    [GeneratedRegex(@"^\s*(?<file>[^\s(][^(]*?)\((?<line>\d+),(?<col>\d+)\)\s*:\s+(?<sev>error|warning|info)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerRegex();

    [GeneratedRegex(@"^\s*(?:CSC\s*:\s*)?(?<sev>error|warning)\s+(?<code>CS\d+)\s*:\s*(?<msg>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ToolDiagnosticRegex();

    [GeneratedRegex(@"\b(?:Csc|ILPostProcess\w*)\s+Library/Bee/artifacts/[^/\s]+/(?:post-processed/)?(?<asm>[^/]+?)\.dll\b", RegexOptions.CultureInvariant)]
    private static partial Regex BeeNodeRegex();

    [GeneratedRegex(@"Application will exit with return code (?<code>-?\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ExitCodeRegex();

    [GeneratedRegex(@"^Assembly with name '(?<name>[^']+)' already exists \((?<path>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateAsmdefRegex();

    [GeneratedRegex(@"Assembly has reference to non-existent assembly '(?<ref>[^']+)' \((?<path>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MissingReferenceRegex();

    [GeneratedRegex(@"^Script '(?<path>[^']+)' will not be compiled because it exists outside the Assets folder", RegexOptions.CultureInvariant)]
    private static partial Regex SkippedScriptRegex();

    [GeneratedRegex(@"Duplicate assembly '(?<name>[^']+)' with different versions detected, using '(?<used>[^,']+)[^']*' and ignoring '(?<ignored>[^,']+)[^']*'", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateDllRegex();

    [GeneratedRegex(@"Multiple precompiled assemblies with the same name (?<name>\S+?) included (?:on|for) the current platform[^(]*(?:\((?<path>[^)]+)\))?", RegexOptions.CultureInvariant)]
    private static partial Regex MultiplePrecompiledRegex();

    [GeneratedRegex(@"^\s+Package (?<id>[^@\s]+)@\S.* has invalid dependencies", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionPackageRegex();

    [GeneratedRegex(@"^\s+(?<id>[a-z0-9][a-z0-9_-]*(?:\.[a-z0-9_-]+)+)(?:\s+\((?<kind>dependency|test package|test dependency)\))?:\s*(?<reason>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionDependencyRegex();
}
