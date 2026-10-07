namespace BasisPM.Core.Services;

public static class AtomicFile
{
    public static async Task WriteAsync(string path, Func<Stream, Task> write, CancellationToken ct = default)
    {
        var temp = TempPathFor(path);
        try
        {
            await using (var stream = File.Create(temp))
                await write(stream).ConfigureAwait(false);
            for (var attempt = 1; !TryReplace(temp, path, attempt); attempt++)
                await Task.Delay(50 * attempt, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void WriteAllText(string path, string contents)
    {
        var temp = TempPathFor(path);
        try
        {
            File.WriteAllText(temp, contents);
            for (var attempt = 1; !TryReplace(temp, path, attempt); attempt++)
                Thread.Sleep(50 * attempt);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static void KeepUnreadableCopy(string path)
    {
        try { if (File.Exists(path)) File.Copy(path, path + ".corrupt", overwrite: true); }
        catch (Exception ex) { DiagnosticLog.Write($"Keeping a copy of the unreadable file {path}", ex); }
    }

    private static string TempPathFor(string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return $"{full}.{Guid.NewGuid():N}.tmp";
    }

    private static bool TryReplace(string temp, string path, int attempt)
    {
        try
        {
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { DiagnosticLog.Write($"Deleting temporary file {path}", ex); }
    }
}
