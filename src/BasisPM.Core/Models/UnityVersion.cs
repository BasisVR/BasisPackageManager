using System.Globalization;

namespace BasisPM.Core.Models;

public sealed record UnityVersion(int Major, int Minor, int Patch, int ChannelRank, char Channel, int Build)
    : IComparable<UnityVersion>
{
    public static UnityVersion Parse(string s)
    {
        if (!TryParse(s, out var v)) throw new FormatException($"Invalid Unity version: {s}");
        return v;
    }

    public static bool TryParse(string? s, out UnityVersion version)
    {
        version = new UnityVersion(0, 0, 0, 0, '?', 0);
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Trim().Split('.');
        if (parts.Length < 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)) return false;
        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor)) return false;

        var patchAndChannel = parts[2];
        var i = 0;
        while (i < patchAndChannel.Length && char.IsDigit(patchAndChannel[i])) i++;
        if (i == 0) return false;
        if (!int.TryParse(patchAndChannel[..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var patch)) return false;

        var channel = i < patchAndChannel.Length ? char.ToLowerInvariant(patchAndChannel[i]) : 'f';
        var buildStr = i + 1 < patchAndChannel.Length ? patchAndChannel[(i + 1)..] : "0";
        if (!int.TryParse(buildStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var build)) return false;

        version = new UnityVersion(major, minor, patch, RankOf(channel), channel, build);
        return true;
    }

    private static int RankOf(char c) => c switch
    {
        'a' => 0,
        'b' => 1,
        'x' => 2,
        'f' => 3,
        'p' => 4,
        _ => -1,
    };

    public int CompareTo(UnityVersion? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major); if (c != 0) return c;
        c = Minor.CompareTo(other.Minor); if (c != 0) return c;
        c = Patch.CompareTo(other.Patch); if (c != 0) return c;
        c = ChannelRank.CompareTo(other.ChannelRank); if (c != 0) return c;
        return Build.CompareTo(other.Build);
    }
}
