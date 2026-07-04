using System.Diagnostics;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

/// <summary>
/// Opens a link in the user's browser. Because <see cref="ProcessStartInfo.UseShellExecute"/> is on,
/// the OS shell would happily launch a local <c>.exe</c>, a UNC path, or any registered protocol
/// handler if handed one — so this only ever forwards http/https/mailto URLs and drops everything
/// else. Use it for any URL that isn't a hard-coded constant (announcement feeds, catalog data, …).
/// </summary>
public static class ExternalLink
{
    public static void Open(string? url)
    {
        if (!GitUrlPolicy.IsWebUrl(url)) return;
        try { Process.Start(new ProcessStartInfo(url!.Trim()) { UseShellExecute = true }); }
        catch { /* best effort — a failed browser launch shouldn't crash the app */ }
    }

    /// <summary>Opens a local folder in the OS file manager. Only accepts an existing directory.</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }
}
