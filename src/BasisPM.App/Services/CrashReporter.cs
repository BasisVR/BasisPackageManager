using System.Diagnostics;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

/// <summary>
/// Notices when the previous run ended badly so the next launch can offer to file an issue. Two signals:
/// a captured exception (unhandled-exception handlers write a detailed crash file), and an unclean-shutdown
/// marker (written on start, deleted only on a clean exit) — which also catches hangs the user force-closed.
/// </summary>
public static class CrashReporter
{
    private static string Dir => AppDataPaths.Root;
    private static string CrashFile => Path.Combine(Dir, AppDataPaths.CrashFileName);
    private static string MarkerFile => Path.Combine(Dir, AppDataPaths.SessionMarkerFileName);

    /// <summary>Set by the shell so a crash report can include the recent action trail.</summary>
    public static Func<string>? BreadcrumbProvider { get; set; }
    public static Func<string>? VersionProvider { get; set; }

    private static bool _written;
    private static bool _previousUnclean;

    public static void Install()
    {
        // Visual Studio's Stop button terminates a debuggee without giving Avalonia a chance to
        // raise Window.Closed or desktop.Exit. Treating that normal development action as a crash
        // causes the warning on every subsequent run. The attached debugger already surfaces real
        // failures, so do not persist an unclean-session marker for debugger-controlled runs.
        if (Debugger.IsAttached)
        {
            _previousUnclean = false;
            MarkCleanExit();
            return;
        }

        try
        {
            _previousUnclean = IsAbandonedMarker(MarkerFile);
        }
        catch (Exception E)
        {
            DiagnosticLog.Write("Checking the previous session crash marker", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
        ArmSession();

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception, "AppDomain");
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception, "UnobservedTask"); e.SetObserved(); };
    }

    /// <summary>(Re)writes the session marker. Used at startup, and to re-arm after an intentional exit that was aborted.</summary>
    public static void ArmSession()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(MarkerFile,
                $"pid={Environment.ProcessId}\nstarted={DateTime.UtcNow:o}");
        }
        catch (Exception E)
        {
            DiagnosticLog.Write("Writing the current session crash marker", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }

    /// <summary>Clears the marker so the next launch knows the session ended normally (clean exit OR an update restart).</summary>
    public static void MarkCleanExit()
    {
        try
        {
            if (File.Exists(MarkerFile))
            {
                File.Delete(MarkerFile);
            }
        }
        catch (Exception E)
        {
            DiagnosticLog.Write("Clearing the current session crash marker", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }


    public static void Write(Exception? ex, string source)
    {
        if (ex is null || _written)
        {
            return;
        }

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
        catch (Exception E)
        {
            DiagnosticLog.Write($"Writing the crash report for {source}", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
    }

    /// <summary>
    /// What happened last run, consumed: <c>detail</c> is a captured exception report (or null), and
    /// <c>unclean</c> is true when the previous session didn't exit cleanly (a crash OR a force-close).
    /// </summary>
    public static (string? detail, bool unclean) TryTakePending()
    {
        string? detail = null;
        try { if (File.Exists(CrashFile)) { detail = File.ReadAllText(CrashFile); File.Delete(CrashFile); } }
        catch (Exception E)
        {
            DiagnosticLog.Write("Reading the pending crash report", E);
            Console.WriteLine($"{E.Message} {E.StackTrace}");
        }
        if (string.IsNullOrWhiteSpace(detail)) detail = null;
        var unclean = _previousUnclean;
        _previousUnclean = false;
        return (detail, unclean);
    }

    private static bool IsAbandonedMarker(string path)
    {
        if (!File.Exists(path)) return false;

        // New markers identify their owning process. This prevents a second executable or a
        // shutdown/relaunch race from reporting the session that is still running as a crash.
        // Legacy timestamp-only markers are conservatively treated as abandoned once.
        var firstLine = File.ReadLines(path).FirstOrDefault();
        if (firstLine is null || !firstLine.StartsWith("pid=", StringComparison.Ordinal) ||
            !int.TryParse(firstLine.AsSpan(4), out var processId))
            return true;

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            // If process inspection is unavailable, do not show a speculative crash dialog.
            DiagnosticLog.Write($"Checking whether session process {processId} is still running", ex);
            return false;
        }
    }

    private static string? SafeInvoke(Func<string>? f)
    {
        try { return f?.Invoke(); } catch (Exception ex) { DiagnosticLog.Write("Collecting crash report metadata", ex); return null; }
    }
}
