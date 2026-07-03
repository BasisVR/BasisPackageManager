using System.Diagnostics;
using System.Runtime.InteropServices;

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
        if (!TryParse(uri, "install", out var get)) return false;
        request = new DeepLinkRequest(get("id"), get("name"), get("git"), get("repo"));
        return true;
    }

    /// <summary>Parses <c>basispm://bundle?id=…</c> (the website's "Install bundle in app" button).</summary>
    public static bool TryParseBundle(string? uri, out string? id)
    {
        id = null;
        if (!TryParse(uri, "bundle", out var get)) return false;
        id = get("id");
        return !string.IsNullOrWhiteSpace(id);
    }

    // Strips the scheme, checks the host, and returns a query accessor — shared by the link parsers.
    private static bool TryParse(string? uri, string expectedHost, out Func<string, string?> get)
    {
        get = _ => null;
        if (!IsDeepLink(uri)) return false;

        var rest = uri!.Trim()[(Scheme.Length + 3)..];               // strip "basispm://"
        var q = rest.IndexOf('?');
        var host = (q >= 0 ? rest[..q] : rest).Trim('/');
        if (!string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase)) return false;

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
    /// Registers the <c>basispm://</c> scheme so the OS routes links to this app.
    /// Only for installed (Velopack) Windows builds — dev runs are tested by launching
    /// the exe with the URI argument directly.
    /// </summary>
    public static void RegisterProtocolIfPackaged(bool isPackaged)
    {
        if (!isPackaged || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

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
}
