using System.Text;
using Avalonia.Platform;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

public static class DesktopIntegration
{
    public const string MenuFile = "basispm.desktop";

    public static string? AppImagePath => OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } path ? path : null;

    public static string ApplicationsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications");

    public static string ExecQuote(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c == '%' ? "%%" : c.ToString());
        }
        return quoted.Append('"').ToString().Replace("\\", "\\\\");
    }

    public static string MenuEntry(string exe, string? icon) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=Basis Package Manager\n" +
        "Comment=Clone Basis, add packages and keep them up to date\n" +
        $"Exec={ExecQuote(exe)} %u\n" +
        (icon is null ? "" : $"Icon={icon}\n") +
        "Terminal=false\n" +
        "Categories=Development;\n" +
        "StartupWMClass=BasisPM.App\n" +
        $"MimeType=x-scheme-handler/{DeepLink.Scheme};\n";

    public static void AddToApplicationsMenu()
    {
        var exe = AppImagePath ?? throw new InvalidOperationException("Only the AppImage can be added to the applications menu.");
        Directory.CreateDirectory(ApplicationsFolder);
        File.WriteAllText(Path.Combine(ApplicationsFolder, MenuFile), MenuEntry(exe, WriteIcon()));
        DeepLink.RunQuiet("update-desktop-database", ApplicationsFolder);
    }

    public static void RefreshApplicationsMenu()
    {
        try
        {
            var file = Path.Combine(ApplicationsFolder, MenuFile);
            if (AppImagePath is not { } exe || !File.Exists(file)) return;
            var icon = Path.Combine(AppDataPaths.LocalRoot, "basispm.png");
            var entry = MenuEntry(exe, File.Exists(icon) ? icon : null);
            if (File.ReadAllText(file) != entry) File.WriteAllText(file, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Updating the applications menu entry", ex); }
    }

    private static string? WriteIcon()
    {
        try
        {
            Directory.CreateDirectory(AppDataPaths.LocalRoot);
            var path = Path.Combine(AppDataPaths.LocalRoot, "basispm.png");
            using var source = AssetLoader.Open(new Uri("avares://BasisPM.App/Assets/BasisLogo.png"));
            using var target = File.Create(path);
            source.CopyTo(target);
            return path;
        }
        catch (Exception ex) { DiagnosticLog.Write("Saving the applications menu icon", ex); return null; }
    }
}
