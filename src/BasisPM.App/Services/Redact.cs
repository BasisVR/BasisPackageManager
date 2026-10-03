using System.Text.RegularExpressions;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

/// <summary>
/// Strips personally-identifying bits — the OS username, machine name, home path, and IP addresses —
/// from any text before it's written to a log/crash file or placed in a GitHub issue.
/// </summary>
public static class Redact
{
    // Environment access is lazy and isolated from type initialization. A transient environment
    // failure can no longer poison the Redact type with a TypeInitializationException.
    private static class EnvironmentValues
    {
        internal static readonly string User = SafeGet(() => Environment.UserName);
        internal static readonly string Machine = SafeGet(() => Environment.MachineName);
        internal static readonly string Profile = SafeGet(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    // C:\Users\<name>\  ·  /home/<name>/  ·  /Users/<name>/
    private static readonly Regex WinUsersPath = new(@"([\\/])Users\1[^\\/\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NixUsersPath = new(@"/home/[^/\r\n]+|/Users/[^/\r\n]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Ipv4 = new(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.Compiled);

    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var s = text;

        if (EnvironmentValues.Profile.Length > 0) s = s.Replace(EnvironmentValues.Profile, "<home>", StringComparison.OrdinalIgnoreCase);
        s = WinUsersPath.Replace(s, "$1Users$1<user>");
        s = NixUsersPath.Replace(s, m => m.Value.StartsWith("/home", StringComparison.OrdinalIgnoreCase) ? "/home/<user>" : "/Users/<user>");

        // Catch the bare username / machine name anywhere (guard against very short, common tokens).
        if (EnvironmentValues.User.Length > 2) s = Regex.Replace(s, Regex.Escape(EnvironmentValues.User), "<user>", RegexOptions.IgnoreCase);
        if (EnvironmentValues.Machine.Length > 2) s = Regex.Replace(s, Regex.Escape(EnvironmentValues.Machine), "<machine>", RegexOptions.IgnoreCase);

        s = Ipv4.Replace(s, "<ip>");
        return s;
    }

    private static string SafeGet(Func<string?> f)
    {
        try { return f() ?? ""; } catch (Exception ex) { DiagnosticLog.Write("Reading environment data for log redaction", ex); return ""; }
    }
}
