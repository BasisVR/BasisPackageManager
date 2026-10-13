using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<IssueSeverity>))]
public enum IssueSeverity { Info, Warning, Error }

public sealed record PackageIssue(string Code, IssueSeverity Severity, string Package, string Message)
{
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Other { get; init; }
    public string? Fix { get; init; }

    public string Location => File is null ? "" : Line is null ? File : Column is null ? $"{File}({Line})" : $"{File}({Line},{Column})";
}

public static class IssueCodes
{
    public const string ManifestMissing = "manifest-missing";
    public const string ManifestInvalid = "manifest-invalid";
    public const string NameInvalid = "name-invalid";
    public const string NameMismatch = "name-mismatch";
    public const string VersionInvalid = "version-invalid";
    public const string DisplayNameMissing = "display-name-missing";
    public const string DescriptionMissing = "description-missing";
    public const string UnityInvalid = "unity-invalid";
    public const string UnityNewer = "unity-newer";
    public const string LicenseMissing = "license-missing";
    public const string ReadmeMissing = "readme-missing";
    public const string DependencyUrl = "dependency-url";
    public const string DependencyVersionInvalid = "dependency-version-invalid";
    public const string DependencyMissing = "dependency-missing";
    public const string DependencyInstall = "dependency-install";
    public const string DependencyTooOld = "dependency-too-old";
    public const string DependencyConflict = "dependency-conflict";
    public const string VpmDependencyMissing = "vpm-dependency-missing";
    public const string PackageDuplicate = "package-duplicate";
    public const string NotAPackage = "not-a-package";
    public const string ScriptsOutsideAssembly = "scripts-outside-asmdef";
    public const string EditorCodeInRuntime = "editor-code-in-runtime";
    public const string SamplesVisible = "samples-not-hidden";
    public const string TestsNotConstrained = "tests-not-constrained";
    public const string MetaMissing = "meta-missing";
    public const string JunkFolder = "junk-folder";
    public const string AsmdefInvalid = "asmdef-invalid";
    public const string AssemblyDuplicate = "assembly-duplicate";
    public const string AssemblyReferenceMissing = "assembly-reference-missing";
    public const string GlobalNamespace = "global-namespace";
    public const string GlobalTypeShadows = "global-type-shadows";
    public const string TypeDuplicate = "type-duplicate";
    public const string NamespaceTypeClash = "namespace-type-clash";
    public const string NamespaceMissing = "namespace-missing";
    public const string NamespaceNotReferenced = "namespace-not-referenced";
    public const string ForeignNamespace = "foreign-namespace";
    public const string DllDuplicate = "dll-duplicate";
    public const string GuidDuplicate = "guid-duplicate";
    public const string CompileError = "compile-error";
    public const string CompileWarning = "compile-warning";
    public const string PackageResolution = "package-resolution";
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageOrigin>))]
public enum PackageOrigin { Project, Embedded, Local, Cache }

public sealed record ProjectPackageRef(string Id, string Root, PackageOrigin Origin)
{
    public bool Immutable => Origin == PackageOrigin.Cache;
}

[JsonConverter(typeof(JsonStringEnumConverter<CompileDiagnosticKind>))]
public enum CompileDiagnosticKind { Compiler, PackageResolution, AssemblyDefinition, Other }

public sealed record CompileDiagnostic(CompileDiagnosticKind Kind, IssueSeverity Severity, string Code, string Message)
{
    public string? File { get; init; }
    public int? Line { get; init; }
    public int? Column { get; init; }
    public string? Assembly { get; init; }
    public string? Package { get; init; }

    public string Location => File is null ? "" : Line is null ? File : Column is null ? $"{File}({Line})" : $"{File}({Line},{Column})";
}

[JsonConverter(typeof(JsonStringEnumConverter<CompileCheckStatus>))]
public enum CompileCheckStatus { Passed, Failed, PackagesFailed, EditorMissing, ProjectOpen, TimedOut, Cancelled, Crashed, Waiting, Inconclusive, NoLicense }

[JsonConverter(typeof(JsonStringEnumConverter<CompileCheckMode>))]
public enum CompileCheckMode { Auto, Batch, OpenEditor }

public sealed class CompileCheckResult
{
    public CompileCheckStatus Status { get; init; }
    public CompileCheckMode Mode { get; init; }
    public string? Detail { get; init; }
    public List<CompileDiagnostic> Diagnostics { get; init; } = new();
    public List<string> FailedAssemblies { get; init; } = new();
    public string? LogPath { get; init; }
    public string? UnityVersion { get; init; }
    public string? EditorPath { get; init; }
    public int? ExitCode { get; init; }
    public DateTimeOffset Started { get; init; }
    public TimeSpan Duration { get; init; }

    [JsonIgnore] public bool Ran => Status is CompileCheckStatus.Passed or CompileCheckStatus.Failed or CompileCheckStatus.PackagesFailed;
    [JsonIgnore] public int ErrorCount => Diagnostics.Count(d => d.Severity == IssueSeverity.Error);
    [JsonIgnore] public int WarningCount => Diagnostics.Count(d => d.Severity == IssueSeverity.Warning);
}
