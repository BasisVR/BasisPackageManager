namespace BasisPM.Core.Services;

/// <summary>
/// Canonical per-user application paths. Environment-dependent values are calculated on access,
/// avoiding static-constructor failures and keeping every component on the same directory layout.
/// </summary>
public static class AppDataPaths
{
    public const string ApplicationDirectoryName = "BasisPM";
    public const string LogsDirectoryName = "logs";
    public const string SettingsFileName = "settings.json";
    public const string MountsFileName = "mounts.json";
    public const string DiagnosticLogFileName = "diagnostic.log";
    public const string CrashFileName = "lastcrash.txt";
    public const string SessionMarkerFileName = "session.lock";

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ApplicationDirectoryName);

    public static string Logs => Path.Combine(Root, LogsDirectoryName);
}
