namespace BasisPM.App.Services;

/// <summary>
/// Notices when the previous run ended badly so the next launch can offer to file an issue. Two signals:
/// a captured exception (unhandled-exception handlers write a detailed crash file), and an unclean-shutdown
/// marker (written on start, deleted only on a clean exit) — which also catches hangs the user force-closed.
/// </summary>
public static class CrashReporter
{
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BasisPM");
    private static string CrashFile => Path.Combine(Dir, "lastcrash.txt");
    private static string MarkerFile => Path.Combine(Dir, "session.lock");

    /// <summary>Set by the shell so a crash report can include the recent action trail.</summary>
    public static Func<string>? BreadcrumbProvider { get; set; }
    public static Func<string>? VersionProvider { get; set; }

    private static bool _written;
    private static bool _previousUnclean;

    public static void Install()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            _previousUnclean = File.Exists(MarkerFile);   // a leftover marker = last session didn't exit cleanly
            File.WriteAllText(MarkerFile, DateTime.UtcNow.ToString("o"));
        }
        catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "UnobservedTask"); e.SetObserved(); };
    }

    /// <summary>Call on a clean shutdown so the next launch knows the session ended normally.</summary>
    public static void MarkCleanExit()
    {
        try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); } catch { }
    }

    public static void Write(Exception? ex, string source)
    {
        if (ex is null || _written) return;
        _written = true;   // keep the first (usually root) crash, not a cascade
        try
        {
            Directory.CreateDirectory(Dir);
            var crumbs = SafeInvoke(BreadcrumbProvider);
            var text =
                $"time: {DateTime.UtcNow:o}\n" +
                $"source: {source}\n" +
                $"app: {SafeInvoke(VersionProvider) ?? "?"}\n" +
                $"os: {Environment.OSVersion}\n" +
                (string.IsNullOrWhiteSpace(crumbs) ? "" : $"breadcrumbs:\n{crumbs}\n") +
                $"exception:\n{ex}";
            File.WriteAllText(CrashFile, Redact.Scrub(text));
        }
        catch { }
    }

    /// <summary>
    /// What happened last run, consumed: <c>detail</c> is a captured exception report (or null), and
    /// <c>unclean</c> is true when the previous session didn't exit cleanly (a crash OR a force-close).
    /// </summary>
    public static (string? detail, bool unclean) TryTakePending()
    {
        string? detail = null;
        try { if (File.Exists(CrashFile)) { detail = File.ReadAllText(CrashFile); File.Delete(CrashFile); } }
        catch { }
        if (string.IsNullOrWhiteSpace(detail)) detail = null;
        return (detail, _previousUnclean);
    }

    private static string? SafeInvoke(Func<string>? f)
    {
        try { return f?.Invoke(); } catch { return null; }
    }
}
