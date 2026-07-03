using System.Globalization;

namespace BasisPM.Core;

public sealed record SemVer(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SemVer>
{
    public static SemVer Parse(string s)
    {
        if (!TryParse(s, out var v)) throw new FormatException($"Invalid semver: {s}");
        return v;
    }

    public static bool TryParse(string? s, out SemVer version)
    {
        version = new SemVer(0, 0, 0);
        if (string.IsNullOrWhiteSpace(s)) return false;
        var trimmed = s.Trim().TrimStart('v', 'V');
        var pre = "";
        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            pre = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];
        }
        var parts = trimmed.Split('.');
        if (parts.Length < 1 || parts.Length > 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var maj)) return false;
        var min = 0;
        var pat = 0;
        if (parts.Length >= 2 && !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out min)) return false;
        if (parts.Length == 3 && !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out pat)) return false;
        version = new SemVer(maj, min, pat, string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1;
        if (other.PreRelease is null) return -1;
        return string.CompareOrdinal(PreRelease, other.PreRelease);
    }

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

public sealed class SemVerRange
{
    private readonly Func<SemVer, bool> _check;
    private readonly string _spec;

    private SemVerRange(string spec, Func<SemVer, bool> check) { _spec = spec; _check = check; }

    public bool Satisfies(SemVer v) => _check(v);
    public override string ToString() => _spec;

    public static SemVerRange Parse(string spec)
    {
        var s = spec.Trim();
        if (s.Length == 0 || s == "*") return new SemVerRange(spec, _ => true);

        if (s.StartsWith("^"))
        {
            var v = SemVer.Parse(s[1..]);
            return new SemVerRange(spec, x => x.CompareTo(v) >= 0 && x.Major == v.Major);
        }
        if (s.StartsWith("~"))
        {
            var v = SemVer.Parse(s[1..]);
            return new SemVerRange(spec, x => x.CompareTo(v) >= 0 && x.Major == v.Major && x.Minor == v.Minor);
        }
        if (s.StartsWith(">="))
        {
            var v = SemVer.Parse(s[2..]);
            return new SemVerRange(spec, x => x.CompareTo(v) >= 0);
        }
        if (s.StartsWith("<="))
        {
            var v = SemVer.Parse(s[2..]);
            return new SemVerRange(spec, x => x.CompareTo(v) <= 0);
        }
        if (s.StartsWith(">"))
        {
            var v = SemVer.Parse(s[1..]);
            return new SemVerRange(spec, x => x.CompareTo(v) > 0);
        }
        if (s.StartsWith("<"))
        {
            var v = SemVer.Parse(s[1..]);
            return new SemVerRange(spec, x => x.CompareTo(v) < 0);
        }

        var exact = SemVer.Parse(s);
        return new SemVerRange(spec, x => x.CompareTo(exact) == 0);
    }
}
