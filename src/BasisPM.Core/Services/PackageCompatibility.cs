using System.Globalization;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public enum CompatibilityProblem { None, NeedsNewerUnity, NeedsNewerBasis, NeedsOlderBasis, NeedsOtherBasisChannel }

public sealed record CompatibilityResult(CompatibilityProblem Problem, string? Required)
{
    public static readonly CompatibilityResult Compatible = new(CompatibilityProblem.None, null);
    public bool Ok => Problem == CompatibilityProblem.None;

    public string Describe() => Problem switch
    {
        CompatibilityProblem.NeedsNewerUnity => $"needs Unity {Required} or newer",
        CompatibilityProblem.NeedsNewerBasis => $"needs Basis from {Required} or newer",
        CompatibilityProblem.NeedsOlderBasis => $"works with Basis from before {Required} only",
        CompatibilityProblem.NeedsOtherBasisChannel => $"needs the Basis {Required} branch",
        _ => "works with this project",
    };
}

public sealed record ProjectTarget(UnityVersion? Unity, DateOnly? Basis, string? BasisBranch = null)
{
    public static readonly ProjectTarget Unknown = new(null, null);
    public bool IsKnown => Unity is not null || Basis is not null || BasisBranch is not null;

    public static ProjectTarget From(string? unityVersion, DateOnly? basis, string? basisBranch = null) =>
        new(UnityVersion.TryParse(unityVersion, out var unity) ? unity : null, basis, string.IsNullOrWhiteSpace(basisBranch) ? null : basisBranch.Trim());
}

public static class PackageCompatibility
{
    private static readonly Regex DateDigits = new(@"(\d{4})[-.]?(\d{2})[-.]?(\d{2})", RegexOptions.CultureInvariant);
    private static readonly Regex UnityMajorMinor = new(@"^\s*(\d+)\.(\d+)\s*$", RegexOptions.CultureInvariant);

    public static CompatibilityResult Check(UpmPackageJson? package, ProjectTarget target) =>
        package is null ? CompatibilityResult.Compatible : Check(package.Unity, package.UnityRelease, package.Basis, target);

    public static CompatibilityResult Check(CatalogPackageVersion? version, ProjectTarget target) =>
        version is null ? CompatibilityResult.Compatible : Check(version.Unity, version.UnityRelease, version.Basis, target);

    public static CompatibilityResult Check(string? unity, string? unityRelease, BasisCompatibility? basis, ProjectTarget target)
    {
        if (target.Unity is { } project && MinimumUnity(unity, unityRelease) is { } minimum && Compare(project, minimum) < 0)
            return new CompatibilityResult(CompatibilityProblem.NeedsNewerUnity, DescribeUnity(unity, unityRelease));
        if (basis?.Channels is { Count: > 0 } channels && target.BasisBranch is { } branch && !MatchesChannels(channels, branch))
            return new CompatibilityResult(CompatibilityProblem.NeedsOtherBasisChannel, string.Join(" or ", channels));
        if (target.Basis is { } basisDate && basis is not null)
        {
            if (ParseBasisDate(basis.From) is { } from && basisDate < from)
                return new CompatibilityResult(CompatibilityProblem.NeedsNewerBasis, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (ParseBasisDate(basis.Before) is { } before && basisDate >= before)
                return new CompatibilityResult(CompatibilityProblem.NeedsOlderBasis, before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return CompatibilityResult.Compatible;
    }

    public static UnityMinimum? MinimumUnity(string? unity, string? unityRelease)
    {
        if (string.IsNullOrWhiteSpace(unity)) return null;
        var match = UnityMajorMinor.Match(unity);
        if (!match.Success)
            return UnityVersion.TryParse(unity, out var full) ? new UnityMinimum(full.Major, full.Minor, full) : null;
        var major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var release = string.IsNullOrWhiteSpace(unityRelease) ? null
            : UnityVersion.TryParse($"{major}.{minor}.{unityRelease.Trim()}", out var parsed) ? parsed : null;
        return new UnityMinimum(major, minor, release);
    }

    public static string DescribeUnity(string? unity, string? unityRelease) =>
        string.IsNullOrWhiteSpace(unityRelease) ? (unity ?? "").Trim() : $"{(unity ?? "").Trim()}.{unityRelease.Trim()}";

    public static int Compare(UnityVersion project, UnityMinimum minimum)
    {
        var c = project.Major.CompareTo(minimum.Major);
        if (c != 0) return c;
        c = project.Minor.CompareTo(minimum.Minor);
        if (c != 0 || minimum.Release is null) return c;
        return project.CompareTo(minimum.Release);
    }

    public static DateOnly? ParseBasisDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = DateDigits.Match(value);
        if (!match.Success) return null;
        var year = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var day = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return null;
        return new DateOnly(year, month, day);
    }

    public static bool IsValidBasisDate(string? value) => ParseBasisDate(value) is not null;

    public const string DeveloperChannel = "developer";
    public const string LtsChannel = "lts";
    public const string ExperimentalChannel = "experimental";

    public static string? ChannelOf(string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var name = branch.Trim();
        if (string.Equals(name, "developer", StringComparison.OrdinalIgnoreCase)) return DeveloperChannel;
        if (name.StartsWith("long-term-support", StringComparison.OrdinalIgnoreCase)) return LtsChannel;
        if (name.StartsWith("experimental", StringComparison.OrdinalIgnoreCase)) return ExperimentalChannel;
        return null;
    }

    public static bool MatchesChannels(IReadOnlyCollection<string>? channels, string? branch)
    {
        if (channels is not { Count: > 0 } || string.IsNullOrWhiteSpace(branch)) return true;
        var channel = ChannelOf(branch);
        foreach (var wanted in channels)
        {
            if (string.IsNullOrWhiteSpace(wanted)) continue;
            var value = wanted.Trim();
            if (string.Equals(value, branch.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
            var alias = string.Equals(value, "long-term-support", StringComparison.OrdinalIgnoreCase) ? LtsChannel : value;
            if (channel is not null && string.Equals(alias, channel, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

public sealed record UnityMinimum(int Major, int Minor, UnityVersion? Release);
