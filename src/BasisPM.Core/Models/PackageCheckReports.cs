using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

[JsonConverter(typeof(CamelCaseEnumConverter<PackageVerdict>))]
public enum PackageVerdict { Unknown, Works, Warnings, Breaks }

public sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase) where TEnum : struct, Enum;

public sealed class BasisCheckTarget
{
    public string Branch { get; set; } = "";
    public string? Commit { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string? Source { get; set; }
    public string? Unity { get; set; }
}

public sealed class PackageCheckState
{
    public PackageVerdict Verdict { get; set; }
    public string? Version { get; set; }
    public string? Commit { get; set; }
    public int Errors { get; set; }
    public int Warnings { get; set; }
    public string? Summary { get; set; }
    public bool Compiled { get; set; }
    public DateTimeOffset? Checked { get; set; }
    public string? BasisBranch { get; set; }
    public string? BasisCommit { get; set; }
    public string? Unity { get; set; }
    public string? Tool { get; set; }
}

public sealed class ProjectCheckReport
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string Project { get; set; } = "";
    public string? Reason { get; set; }
    public List<string> Focus { get; set; } = new();
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset Finished { get; set; }
    public string? UnityVersion { get; set; }
    public BasisCheckTarget? Basis { get; set; }
    public List<PackageIssue> Issues { get; set; } = new();
    public CompileCheckResult? Compile { get; set; }
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Dictionary<string, PackageCheckState> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Introduced { get; set; } = new();
    public List<string>? Uncompiled { get; set; }
    public Dictionary<string, List<string>>? Attributed { get; set; }
    public List<AppliedApiUpgrade>? Upgrades { get; set; }
    public string? Tool { get; set; }

    [JsonIgnore] public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);
    [JsonIgnore] public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    [JsonIgnore] public bool HasErrors => ErrorCount > 0;

    public IEnumerable<PackageIssue> IssuesFor(string packageId) => Issues.Where(i => string.Equals(i.Package, packageId, StringComparison.OrdinalIgnoreCase));

    public bool IsIntroduced(PackageIssue issue) => Introduced.Contains(PackageCheckKeys.Of(issue));
}

public sealed record AppliedApiUpgrade(string Package, string File, int Line, string From, string To);

public sealed class ProjectCheckHistory
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public string Project { get; set; } = "";
    public ProjectCheckReport? Last { get; set; }
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Dictionary<string, PackageCheckState> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class RegistryChecksFile
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public DateTimeOffset? Generated { get; set; }
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Dictionary<string, BasisCheckTarget> Basis { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonObjectCreationHandling(JsonObjectCreationHandling.Populate)] public Dictionary<string, Dictionary<string, PackageCheckState>> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class PackageCheckKeys
{
    public static string Of(PackageIssue issue) => string.Join('|', issue.Code, issue.Package.ToLowerInvariant(), (issue.File ?? "").Replace('\\', '/'), issue.Line?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "", issue.Message);
}
