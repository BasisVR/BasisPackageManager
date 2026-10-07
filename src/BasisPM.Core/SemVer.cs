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
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];
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

    public static bool TryParseTag(string? tag, out SemVer version)
    {
        if (TryParse(tag, out version)) return true;
        var slash = tag?.LastIndexOf('/') ?? -1;
        return slash >= 0 && TryParse(tag![(slash + 1)..], out version);
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
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNumeric = long.TryParse(x[i], NumberStyles.None, CultureInfo.InvariantCulture, out var xn);
            var yNumeric = long.TryParse(y[i], NumberStyles.None, CultureInfo.InvariantCulture, out var yn);
            var c = xNumeric && yNumeric ? xn.CompareTo(yn)
                : xNumeric ? -1
                : yNumeric ? 1
                : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }

    public override string ToString() =>
        PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

public sealed class SemVerRange
{
    private static readonly string[] Operators = { ">=", "<=", ">", "<", "=", "^", "~" };

    private readonly Func<SemVer, bool> _check;
    private readonly string _spec;

    private SemVerRange(string spec, Func<SemVer, bool> check) { _spec = spec; _check = check; }

    public bool Satisfies(SemVer v) => _check(v);
    public override string ToString() => _spec;

    public static bool TryParse(string? spec, out SemVerRange? range)
    {
        try
        {
            range = Parse(spec ?? "");
            return true;
        }
        catch (FormatException)
        {
            range = null;
            return false;
        }
    }

    public static SemVerRange Parse(string spec)
    {
        var s = spec.Trim();
        if (s.Length == 0 || s is "*" or "x" or "X") return new SemVerRange(spec, _ => true);

        if (s.Contains("||", StringComparison.Ordinal))
        {
            var alternatives = s.Split("||").Select(Parse).ToList();
            return new SemVerRange(spec, x => alternatives.Any(a => a.Satisfies(x)));
        }

        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 3 && tokens[1] == "-")
        {
            var low = SemVer.Parse(tokens[0]);
            var high = SemVer.Parse(tokens[2]);
            return new SemVerRange(spec, x => x.CompareTo(low) >= 0 && x.CompareTo(high) <= 0);
        }
        if (tokens.Length > 1)
        {
            var comparators = new List<SemVerRange>();
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = Operators.Contains(tokens[i]) && i + 1 < tokens.Length ? tokens[i] + tokens[++i] : tokens[i];
                comparators.Add(ParseComparator(token));
            }
            return new SemVerRange(spec, x => comparators.All(c => c.Satisfies(x)));
        }

        return ParseComparator(s, spec);
    }

    private static SemVerRange ParseComparator(string s, string? spec = null)
    {
        spec ??= s;
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
        if (s.StartsWith("=")) s = s[1..];

        if (WildcardBounds(s) is { } bounds)
            return new SemVerRange(spec, x => x.CompareTo(bounds.Low) >= 0 && x.CompareTo(bounds.High) < 0);

        var exact = SemVer.Parse(s);
        return new SemVerRange(spec, x => x.CompareTo(exact) == 0);
    }

    private static (SemVer Low, SemVer High)? WildcardBounds(string s)
    {
        var parts = s.TrimStart('v', 'V').Split('.');
        if (parts.Length is < 2 or > 3 || !parts.Skip(1).Any(IsWildcard)) return null;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return null;
        if (IsWildcard(parts[1])) return (new SemVer(major, 0, 0), new SemVer(major + 1, 0, 0, "0"));
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        return (new SemVer(major, minor, 0), new SemVer(major, minor + 1, 0, "0"));
    }

    private static bool IsWildcard(string part) => part is "x" or "X" or "*";
}
