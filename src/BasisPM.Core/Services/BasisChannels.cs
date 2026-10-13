using System.Globalization;
using System.Text.RegularExpressions;

namespace BasisPM.Core.Services;

public enum BasisChannel { Developer, LongTermSupport, Experimental, Other }

public static partial class BasisChannels
{
    public static BasisChannel Classify(string? branch) => PackageCompatibility.ChannelOf(branch) switch
    {
        PackageCompatibility.DeveloperChannel => BasisChannel.Developer,
        PackageCompatibility.LtsChannel => BasisChannel.LongTermSupport,
        PackageCompatibility.ExperimentalChannel => BasisChannel.Experimental,
        _ => BasisChannel.Other,
    };

    public static string? Key(BasisChannel channel) => channel switch
    {
        BasisChannel.Developer => PackageCompatibility.DeveloperChannel,
        BasisChannel.LongTermSupport => PackageCompatibility.LtsChannel,
        BasisChannel.Experimental => PackageCompatibility.ExperimentalChannel,
        _ => null,
    };

    public static DateOnly? LongTermSupportDate(string? branch)
    {
        if (Classify(branch) != BasisChannel.LongTermSupport) return null;
        var match = DateDigits().Match(branch!);
        return match.Success && DateOnly.TryParseExact(match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    public static string Describe(string? branch)
    {
        var name = (branch ?? "").Trim();
        return Classify(name) switch
        {
            BasisChannel.Developer => PackageCompatibility.DeveloperChannel,
            BasisChannel.LongTermSupport => LongTermSupportDate(name) is { } date ? "LTS " + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "LTS",
            BasisChannel.Experimental => PackageCompatibility.ExperimentalChannel,
            _ => name.Length == 0 ? "unknown" : name,
        };
    }

    public static string? FindRecordKey(IEnumerable<string> keys, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var list = keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
        return list.FirstOrDefault(k => string.Equals(k.Trim(), branch.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? (Classify(branch) == BasisChannel.Other ? null : list.FirstOrDefault(k => PackageCompatibility.MatchesChannels(new[] { k }, branch)));
    }

    [GeneratedRegex(@"(\d{4})-?(\d{2})-?(\d{2})", RegexOptions.CultureInvariant)]
    private static partial Regex DateDigits();
}
