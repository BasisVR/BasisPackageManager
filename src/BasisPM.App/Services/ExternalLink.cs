using BasisPM.Core.Services;
using System.Diagnostics;

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
        if (!GitUrlPolicy.IsWebUrl(url))
        {
            return;
        }

        try { Process.Start(new ProcessStartInfo(url!.Trim()) { UseShellExecute = true }); }
        catch (Exception E)
        {
            DiagnosticLog.Write($"Opening external link {url}", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }

    private static readonly HashSet<string> OpenableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".txt", ".md", ".json", ".xml", ".yml", ".yaml", ".asmdef", ".asmref", ".shader", ".hlsl", ".cginc",
        ".compute", ".uss", ".uxml", ".inputactions", ".csv", ".ini", ".cfg", ".props", ".targets", ".gitignore",
        ".gitattributes", ".editorconfig",
    };

    public static bool CanOpenFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && OpenableExtensions.Contains(Path.GetExtension(path));

    public static void OpenFile(string? path)
    {
        if (!CanOpenFile(path) || !File.Exists(path))
        {
            return;
        }

        try { Process.Start(new ProcessStartInfo(path!) { UseShellExecute = true }); }
        catch (Exception E)
        {
            DiagnosticLog.Write($"Opening file {path}", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }

    /// <summary>Opens a local folder in the OS file manager. Only accepts an existing directory.</summary>
    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception E)
        {
            DiagnosticLog.Write($"Opening folder {path}", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }
}
