namespace BasisPM.Core.Services;

public static class FileRetry
{
    private static readonly TimeSpan[] DefaultDelays =
    {
        TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2),
    };

    public static void Run(Action action, IReadOnlyList<TimeSpan>? delays = null)
    {
        var waits = delays ?? DefaultDelays;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < waits.Count && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(waits[attempt]);
            }
        }
    }

    public static void DeleteDirectory(string path, bool keepFolder = false, IReadOnlyList<TimeSpan>? delays = null)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Clearing file attributes for {file}", ex); }
        }
        Run(() =>
        {
            if (!Directory.Exists(path)) return;
            if (!keepFolder)
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).ToList())
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
        }, delays);
    }
}
