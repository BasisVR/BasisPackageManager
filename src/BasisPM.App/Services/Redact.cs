using System.Text.RegularExpressions;

namespace BasisPM.App.Services;

/// <summary>
/// Strips personally-identifying bits — the OS username, machine name, home path, and IP addresses —
/// from any text before it's written to a log/crash file or placed in a GitHub issue.
/// </summary>
public static class Redact
{
    private static readonly string User = SafeGet(() => Environment.UserName);
    private static readonly string Machine = SafeGet(() => Environment.MachineName);
    private static readonly string Profile = SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // C:\Users\<name>\  ·  /home/<name>/  ·  /Users/<name>/
    private static readonly Regex WinUsersPath = new(@"([\\/])Users\1[^\\/\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NixUsersPath = new(@"/home/[^/\r\n]+|/Users/[^/\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;

        if (Profile.Length > 0) s = s.Replace(Profile, "<home>", StringComparison.OrdinalIgnoreCase);
        s = WinUsersPath.Replace(s, "$1Users$1<user>");
        s = NixUsersPath.Replace(s, m => m.Value.StartsWith("/home", StringComparison.OrdinalIgnoreCase) ? "/home/<user>" : "/Users/<user>");

        // Catch the bare username / machine name anywhere (guard against very short, common tokens).
        if (User.Length > 2) s = Regex.Replace(s, Regex.Escape(User), "<user>", RegexOptions.IgnoreCase);
        if (Machine.Length > 2) s = Regex.Replace(s, Regex.Escape(Machine), "<machine>", RegexOptions.IgnoreCase);

        s = Ipv4.Replace(s, "<ip>");
        return s;
    }

    private static string SafeGet(Func<string?> f)
    {
        try { return f() ?? ""; } catch { return ""; }
    }
}
