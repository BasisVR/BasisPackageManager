using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace BasisPM.Core.Services;

/// <summary>
/// Last-resort diagnostic logging for exceptions that are handled and allowed to recover.
/// This deliberately has no dependencies and never throws so it is safe during startup,
/// shutdown, crash reporting, and error handling itself.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 5 * 1024 * 1024;

    public static string LogDirectory => AppDataPaths.Logs;

    public static string FilePath => Path.Combine(LogDirectory, AppDataPaths.DiagnosticLogFileName);

    public static void Write(string context, Exception exception)
    {
        try
        {
            var entry = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [handled] ").Append(context)
                .Append(" | process=").Append(Environment.ProcessId)
                .Append(" thread=").Append(Environment.CurrentManagedThreadId)
                .AppendLine()
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(FilePath, entry, Encoding.UTF8);
            }
            Debug.WriteLine(entry);
        }
        catch
        {
            // A diagnostic logger must never replace the original handled failure with a new one.
        }
    }

    private static void RotateIfNeeded()
    {
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length < MaxBytes) return;
        var previous = FilePath + ".previous";
        File.Move(FilePath, previous, overwrite: true);
    }
}
