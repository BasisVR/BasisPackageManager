using System.Diagnostics;
using System.Globalization;
using System.Security;
using System.Text;

namespace BasisPM.Core.Services;

/// <summary>One reminder the operating system should start the app for.</summary>
public sealed record OsReminder(string EventId, DateTimeOffset FireAt, bool Weekly);

/// <summary>
/// Registers event reminders with the operating system, so they are shown while the Package Manager is closed.
/// On Windows each reminder is a Task Scheduler task that starts the app with <see cref="Argument"/>.
/// </summary>
public static class OsReminderScheduler
{
    /// <summary>The command-line argument a scheduled start passes: show what is due, then exit.</summary>
    public const string Argument = "--event-reminders";

    private const string TaskPrefix = "Basis Package Manager event reminder ";

    public static bool IsSupported => OperatingSystem.IsWindows();

    public static string TaskName(string eventId)
    {
        var safe = new StringBuilder();
        foreach (var c in eventId)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        }
        return TaskPrefix + safe;
    }

    /// <summary>What a registered task was built from; a reminder is registered again only when this changes.</summary>
    public static string Signature(OsReminder reminder, string exe) =>
        EventReminderPlan.Key(reminder.FireAt) + "|" + (reminder.Weekly ? "weekly" : "once") + "|" + exe;

    /// <summary>The Task Scheduler definition for one reminder. Times are UTC, so the task follows the event and not the local clock.</summary>
    public static string TaskXml(OsReminder reminder, string exe)
    {
        var start = Utc(reminder.FireAt);
        var trigger = reminder.Weekly
            ? $"<StartBoundary>{start}</StartBoundary><Repetition><Interval>P7D</Interval></Repetition>"
            : $"<StartBoundary>{start}</StartBoundary><EndBoundary>{Utc(reminder.FireAt.AddDays(1))}</EndBoundary>";
        var expiry = reminder.Weekly ? "" : "<DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>";
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n"
            + "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n"
            + "<RegistrationInfo><Description>Shows a Basis Package Manager event reminder.</Description></RegistrationInfo>\n"
            + $"<Triggers><TimeTrigger>{trigger}<Enabled>true</Enabled></TimeTrigger></Triggers>\n"
            + "<Principals><Principal id=\"Author\"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n"
            + "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
            + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable>"
            + $"<ExecutionTimeLimit>PT1H</ExecutionTimeLimit>{expiry}<Enabled>true</Enabled></Settings>\n"
            + $"<Actions Context=\"Author\"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>{Argument}</Arguments></Exec></Actions>\n"
            + "</Task>\n";
    }

    /// <summary>
    /// Makes the registered tasks match <paramref name="wanted"/>: new and changed reminders are registered, the rest removed.
    /// Returns the event ids now registered with their signatures, to pass back in as <paramref name="known"/> next time.
    /// </summary>
    public static Dictionary<string, string> Sync(IReadOnlyCollection<OsReminder> wanted, IReadOnlyDictionary<string, string> known, string exe)
    {
        var result = new Dictionary<string, string>();
        if (!IsSupported)
        {
            return result;
        }
        foreach (var id in known.Keys)
        {
            if (wanted.All(w => w.EventId != id))
            {
                Remove(id);
            }
        }
        foreach (var reminder in wanted)
        {
            var signature = Signature(reminder, exe);
            if ((known.TryGetValue(reminder.EventId, out var old) && old == signature) || Register(reminder, exe))
            {
                result[reminder.EventId] = signature;
            }
        }
        return result;
    }

    private static bool Register(OsReminder reminder, string exe)
    {
        var file = Path.Combine(Path.GetTempPath(), "basispm-reminder-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(file, TaskXml(reminder, exe), Encoding.Unicode);
            return Run("/Create", "/TN", TaskName(reminder.EventId), "/XML", file, "/F");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write("Writing an event reminder task definition", ex);
            return false;
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write("Deleting an event reminder task definition", ex);
            }
        }
    }

    private static void Remove(string eventId) => Run("/Delete", "/TN", TaskName(eventId), "/F");

    private static bool Run(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { CreateNoWindow = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                return false;
            }
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            DiagnosticLog.Write("Running the Windows task scheduler tool", ex);
            return false;
        }
    }

    private static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
