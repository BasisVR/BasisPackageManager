using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>A cached git package whose files differ from its pristine source — likely an accidental in-cache edit.</summary>
public sealed record CacheDrift(string PackageId, string GitUrl, string CacheFolder, string WorkClonePath, IReadOnlyList<string> ChangedFiles);

/// <summary>
/// Detects local edits made (usually by accident) to git packages Unity keeps under
/// <c>Library/PackageCache/&lt;id&gt;@&lt;hash&gt;</c>. Those edits are invisible to git and lost on re-resolve.
/// For each git dependency it clones the pristine source at the cached revision, overlays the cache's current
/// files, and asks git what changed — leaving a ready-to-PR work clone so the change can be contributed upstream.
/// </summary>
public sealed class CacheDriftService
{
    private readonly GitService _git;

    public CacheDriftService(GitService git) => _git = git;

    private static string WorkRoot => Path.Combine(Path.GetTempPath(), "BasisPM", "cachedrift");

    // A git checkout writes all of a package's files at ~one instant, so an untouched cache folder has a tiny
    // mtime spread. Under this threshold we skip the (expensive) clone+diff entirely — that's the fast bail-out.
    private static readonly TimeSpan CheckoutSpreadThreshold = TimeSpan.FromSeconds(60);

    public async Task<IReadOnlyList<CacheDrift>> ScanAsync(BasisInstall install, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var results = new List<CacheDrift>();
        var cacheDir = Path.Combine(install.UnityProjectPath, "Library", "PackageCache");
        if (!Directory.Exists(cacheDir)) return results;

        TryDelete(WorkRoot);
        Directory.CreateDirectory(WorkRoot);

        foreach (var (id, value) in install.Manifest.Dependencies)
        {
            ct.ThrowIfCancellationRequested();

            var parsed = UpmGitUrl.Parse(value);
            if (parsed is null || !(parsed.IsGitHub || parsed.IsGitLab)) continue;
            if (!GitUrlPolicy.IsSafeUrl(parsed.CloneUrl)) continue;

            // Unity names the cache folder "<id>@<resolved-revision>".
            var cacheFolder = SafeEnumerate(cacheDir, id + "@*").FirstOrDefault();
            if (cacheFolder is null) continue;
            var folderName = Path.GetFileName(cacheFolder);
            var at = folderName.IndexOf('@');
            if (at < 0) continue;
            var hash = folderName[(at + 1)..];
            if (!GitUrlPolicy.IsSafeRef(hash)) continue;

            // Fast bail-out: skip packages whose cache files all still sit at their checkout time.
            if (!LooksTouched(cacheFolder)) continue;

            onProgress?.Invoke($"Checking {id}…");
            var workClone = Path.Combine(WorkRoot, Sanitize(id));
            var clone = await _git.CloneAtAsync(parsed.CloneUrl, workClone, hash, null, ct).ConfigureAwait(false);
            if (!clone.Ok) { TryDelete(workClone); continue; }

            // Normalize EOLs on staging so CRLF/LF differences from Unity's checkout don't read as edits.
            await _git.SetConfigAsync(workClone, "core.autocrlf", "input", ct).ConfigureAwait(false);

            var pkgDir = parsed.Path is null
                ? workClone
                : Path.Combine(workClone, parsed.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                ClearExcept(pkgDir, parsed.Path is null ? ".git" : null);
                CopyInto(cacheFolder, pkgDir);
            }
            catch { TryDelete(workClone); continue; }

            await _git.AddAllAsync(workClone, ct).ConfigureAwait(false);
            var status = await _git.GetStatusAsync(workClone, ct).ConfigureAwait(false);
            var changed = status.Changes.Select(c => c.Path).ToList();

            if (changed.Count > 0)
                results.Add(new CacheDrift(id, parsed.CloneUrl, cacheFolder, workClone, changed));
            else
                TryDelete(workClone);
        }

        return results;
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try { return Directory.EnumerateDirectories(dir, pattern); }
        catch { return Array.Empty<string>(); }
    }

    // True if a file looks edited after checkout (mtime spread over the threshold). Cheap: file metadata only, no clone.
    private static bool LooksTouched(string folder)
    {
        DateTime min = DateTime.MaxValue, max = DateTime.MinValue;
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var t = File.GetLastWriteTimeUtc(f);
                if (t < min) min = t;
                if (t > max) max = t;
            }
        }
        catch { return true; } // can't tell → don't skip it
        return min != DateTime.MaxValue && (max - min) > CheckoutSpreadThreshold;
    }

    private static string Sanitize(string id) =>
        string.Concat(id.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));

    private static void ClearExcept(string dir, string? keep)
    {
        if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (keep is not null && Path.GetFileName(entry).Equals(keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (Directory.Exists(entry)) ForceDelete(entry);
            else { try { File.SetAttributes(entry, FileAttributes.Normal); } catch { } File.Delete(entry); }
        }
    }

    private static void CopyInto(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            var target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) ForceDelete(path); } catch { }
    }

    private static void ForceDelete(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }
}
