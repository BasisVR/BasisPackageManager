using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public enum PackageRisk { Caution, High }

public enum PackageCapability
{
    RunsInCompiler,
    StartsPrograms,
    LoadsHiddenCode,
    ReadsLogins,
    SystemSettings,
    Programs,
    NativeCode,
    CompiledCode,
    RunsAutomatically,
    Network,
    AddsPackages,
    EncodedData,
}

public sealed record PackageFinding(PackageCapability Capability, PackageRisk Risk, string File, int Line, string Evidence)
{
    public string Location => Line > 0 ? $"{File}:{Line}" : File;
}

public sealed record PackageScan(IReadOnlyList<PackageFinding> Findings, int Scripts, int Binaries, int Files, bool Incomplete)
{
    public static PackageScan Empty { get; } = new(Array.Empty<PackageFinding>(), 0, 0, 0, false);
    public bool HasHighRisk => Findings.Any(f => f.Risk == PackageRisk.High);
    public IReadOnlyList<PackageCapability> Capabilities => Findings.GroupBy(f => f.Capability).OrderByDescending(g => g.Max(f => f.Risk)).ThenBy(g => g.Key).Select(g => g.Key).ToList();
    public PackageRisk RiskOf(PackageCapability capability) => Findings.Where(f => f.Capability == capability).Select(f => f.Risk).DefaultIfEmpty(PackageRisk.Caution).Max();
}

public sealed record PackageReview(string PackageId, string Folder, string PackageRoot, UpmGitUrl Source, string? Commit)
{
    public UpmPackageJson? Package { get; init; }
    public string DisplayName => Package is { DisplayName.Length: > 0 } p ? p.DisplayName : Package is { Name.Length: > 0 } n ? n.Name : PackageId;
}

public enum AdvisorySeverity { Warning, Malware }

public sealed class PackageAdvisory
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("package")] public string Package { get; set; } = "";
    [JsonPropertyName("repository")] public string? Repository { get; set; }
    [JsonPropertyName("commits")] public List<string>? Commits { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("details")] public string? Details { get; set; }
    [JsonPropertyName("published")] public string? Published { get; set; }
    [JsonIgnore] public AdvisorySeverity Level => string.Equals(Severity?.Trim(), "warning", StringComparison.OrdinalIgnoreCase) ? AdvisorySeverity.Warning : AdvisorySeverity.Malware;
    [JsonIgnore] public bool Blocks => Level == AdvisorySeverity.Malware;
}
