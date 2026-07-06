using System.Diagnostics;

namespace BasisPM.App.Services;

public sealed record DeepLinkRequest(string? Id, string? Name, string? Git, string? Repo);

/// <summary>
/// The <c>basispm://install?id=…&amp;name=…&amp;git=…&amp;repo=…</c> deep link used by the
/// registry's "Install in app" button, plus Windows protocol registration.
/// </summary>
public static class DeepLink
{
    public const string Scheme = "basispm";

    public static bool IsDeepLink(string? arg) =>
        !string.IsNullOrEmpty(arg) && arg.StartsWith(Scheme + "://", StringComparison.OrdinalIgnoreCase);

    public static bool TryParseInstall(string? uri, out DeepLinkRequest request)
    {
        request = new DeepLinkRequest(null, null, null, null);
        if (!TryParse(uri, out var get, "install")) return false;
        request = new DeepLinkRequest(get("id"), get("name"), get("git"), get("repo"));
        return true;
    }

    /// <summary>
    /// Parses <c>basispm://packagelist?id=…</c> (the website's "Install package list in app" button).
    /// The legacy <c>basispm://bundle?id=…</c> host is still accepted so links shared before the rename keep working.
    /// </summary>
    public static bool TryParsePackageList(string? uri, out string? id)
    {
        id = null;
        if (!TryParse(uri, out var get, "packagelist", "bundle")) return false;
        id = get("id");
        return !string.IsNullOrWhiteSpace(id);
    }

    // Strips the scheme, checks the host (any of expectedHosts), and returns a query accessor — shared by the link parsers.
    private static bool TryParse(string? uri, out Func<string, string?> get, params string[] expectedHosts)
    {
        get = _ => null;
        if (!IsDeepLink(uri)) return false;

        var rest = uri!.Trim()[(Scheme.Length + 3)..];               // strip "basispm://"
        var q = rest.IndexOf('?');
        var host = (q >= 0 ? rest[..q] : rest).Trim('/');
        var hostMatches = false;
        foreach (var h in expectedHosts)
            if (string.Equals(host, h, StringComparison.OrdinalIgnoreCase)) { hostMatches = true; break; }
        if (!hostMatches) return false;

        var query = q >= 0 ? rest[(q + 1)..] : "";
        get = key =>
        {
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && string.Equals(kv[0], key, StringComparison.OrdinalIgnoreCase))
                {
                    try { return Uri.UnescapeDataString(kv[1]); } catch { return kv[1]; }
                }
            }
            return null;
        };
        return true;
    }

    /// <summary>
    /// Registers the <c>basispm://</c> scheme so the OS routes links to this app. Only for installed
    /// (Velopack) builds — dev runs are tested by launching the exe with the URI argument directly.
    /// Windows writes the HKCU class; Linux writes an XDG <c>.desktop</c> handler; macOS declares the
    /// scheme via <c>CFBundleURLTypes</c> in the app bundle's Info.plist at packaging time (LaunchServices
    /// reads it on install), so there is nothing to register at runtime there.
    /// </summary>
    public static void RegisterProtocolIfPackaged(bool isPackaged)
    {
        if (!isPackaged) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        if (OperatingSystem.IsWindows()) RegisterWindows(exe);
        else if (OperatingSystem.IsLinux()) RegisterLinux(exe);
        // macOS handled at packaging time (Info.plist), see summary.
    }

    private static void RegisterWindows(string exe)
    {
        var root = $@"HKCU\Software\Classes\{Scheme}";
        try
        {
            Reg(root, "/ve", "/d", "URL:Basis Package Manager");
            Reg(root, "/v", "URL Protocol", "/d", "");
            Reg($@"{root}\shell\open\command", "/ve", "/d", $"\"{exe}\" \"%1\"");
        }
        catch { /* best effort — deep links simply won't route until next successful run */ }
    }

    private static void Reg(string key, params string[] rest)
    {
        var psi = new ProcessStartInfo("reg.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("add");
        psi.ArgumentList.Add(key);
        foreach (var r in rest) psi.ArgumentList.Add(r);
        psi.ArgumentList.Add("/f");
        Process.Start(psi)?.WaitForExit(3000);
    }

    /// <summary>The XDG desktop-entry filename that owns the scheme (also the id passed to xdg-mime).</summary>
    public const string LinuxDesktopFile = "basispm-url-handler.desktop";

    /// <summary>The <c>.desktop</c> file body registering this app as the handler for <c>basispm://</c>. Pure, so it's testable.</summary>
    public static string LinuxDesktopEntry(string exe) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Basis Package Manager\n" +
        $"Exec=\"{exe}\" %u\n" +
        "NoDisplay=true\n" +
        "StartupNotify=false\n" +
        "Terminal=false\n" +
        $"MimeType=x-scheme-handler/{Scheme};\n";

    private static void RegisterLinux(string exe)
    {
        try
        {
            var appsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");
            Directory.CreateDirectory(appsDir);
            File.WriteAllText(Path.Combine(appsDir, LinuxDesktopFile), LinuxDesktopEntry(exe));
            // Make this desktop entry the default handler for the scheme, then refresh the DB (both best-effort).
            RunQuiet("xdg-mime", "default", LinuxDesktopFile, $"x-scheme-handler/{Scheme}");
            RunQuiet("update-desktop-database", appsDir);
        }
        catch { /* best effort — deep links simply won't route until next successful run */ }
    }

    private static void RunQuiet(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.WaitForExit(3000);
        }
        catch { }
    }
}
