using System.Security.Cryptography;
using System.Text;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record GitCacheClearResult(int Removed, int Busy, long Freed);

public sealed class GitCloneCache(string? root = null, Func<bool>? isEnabled = null)
{
    public const string MirrorSuffix = ".git";
    public const string LockSuffix = ".lock";
    public static readonly TimeSpan DefaultLockWait = TimeSpan.FromMinutes(2);
    private const int MirrorSearchDepth = 6;

    public string Root { get; } = Path.GetFullPath(root ?? AppDataPaths.GitCache);

    public TimeSpan LockWait { get; init; } = DefaultLockWait;

    public bool IsEnabled
    {
        get
        {
            try { return isEnabled?.Invoke() ?? true; }
            catch (Exception ex)
            {
                DiagnosticLog.Write("Checking whether the clone cache is on", ex);
                return false;
            }
        }
    }

    public string MirrorPathFor(string url)
    {
        var trimmed = url.Trim();
        if (!IsLocalPath(trimmed) && UpmGitUrl.Parse(trimmed) is { } parsed)
        {
            var hosted = parsed.IsGitHub || parsed.IsGitLab;
            var parts = new List<string> { Root, Segment(parsed.Host, hosted) };
            parts.AddRange(parsed.Owner.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(owner => Segment(owner, hosted)));
            parts.Add(Segment(parsed.Repo, hosted) + MirrorSuffix);
            return Path.Combine(parts.ToArray());
        }
        var key = IsLocalPath(trimmed) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed)) : trimmed;
        return Path.Combine(Root, "local", Hash(key) + MirrorSuffix);
    }

    public static bool IsMirror(string folder) =>
        File.Exists(Path.Combine(folder, "HEAD")) && Directory.Exists(Path.Combine(folder, "objects")) && Directory.Exists(Path.Combine(folder, "refs"));

    public async Task<IDisposable?> AcquireAsync(string mirror, TimeSpan wait, CancellationToken ct = default)
    {
        var lockPath = mirror + LockSuffix;
        var deadline = DateTime.UtcNow + wait;
        try { Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            DiagnosticLog.Write($"Creating the clone cache folder for {mirror}", ex);
            return null;
        }
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < deadline) { await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write($"Locking the clone cache entry {mirror}", ex);
                return null;
            }
        }
    }

    public IReadOnlyList<string> ListMirrors()
    {
        var mirrors = new List<string>();
        if (!Directory.Exists(Root)) return mirrors;
        var pending = new Stack<(string Folder, int Depth)>();
        pending.Push((Root, 0));
        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            foreach (var child in SafeDirectories(folder))
            {
                if (child.EndsWith(MirrorSuffix, StringComparison.OrdinalIgnoreCase) && IsMirror(child)) mirrors.Add(child);
                else if (depth + 1 < MirrorSearchDepth) pending.Push((child, depth + 1));
            }
        }
        mirrors.Sort(StringComparer.OrdinalIgnoreCase);
        return mirrors;
    }

    public long MeasureBytes(CancellationToken ct = default) => Directory.Exists(Root) ? FolderSize(Root, ct) : 0;

    public Task<long> MeasureBytesAsync(CancellationToken ct = default) => Task.Run(() => MeasureBytes(ct), ct);

    public GitCacheClearResult Clear()
    {
        int removed = 0, busy = 0;
        long freed = 0;
        foreach (var mirror in ListMirrors())
        {
            using var lease = TryLock(mirror);
            if (lease is null)
            {
                busy++;
                continue;
            }
            var size = FolderSize(mirror, CancellationToken.None);
            try
            {
                FileRetry.DeleteDirectory(mirror);
                removed++;
                freed += size;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Write($"Deleting the cached repository {mirror}", ex);
                busy++;
            }
        }
        RemoveEmptyFolders(Root);
        return new GitCacheClearResult(removed, busy, freed);
    }

    public Task<GitCacheClearResult> ClearAsync() => Task.Run(Clear);

    private IDisposable? TryLock(string mirror)
    {
        try { return new FileStream(mirror + LockSuffix, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsLocalPath(string url) => !url.Contains("://", StringComparison.Ordinal) && (Path.IsPathRooted(url) || Directory.Exists(url));

    private static string Segment(string value, bool lower)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in lower ? value.ToLowerInvariant() : value)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        var text = builder.ToString().Trim('.');
        return text.Length == 0 ? "_" : text;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static IEnumerable<string> SafeDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static long FolderSize(string folder, CancellationToken ct)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += file.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Measuring {folder}", ex); }
        return total;
    }

    private static void RemoveEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var folder in SafeDirectories(root))
        {
            if (folder.EndsWith(MirrorSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            RemoveEmptyFolders(folder);
            try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
