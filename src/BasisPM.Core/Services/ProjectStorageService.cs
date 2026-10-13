using System.Diagnostics;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class ProjectStorageService(BasisDevService basisDev, MountService mounts)
{
    public const string ServerFolderName = "Basis Server";
    private const long ProgressFileStep = 2000;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);
    private static readonly EnumerationOptions Listing = new() { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
    private static readonly StorageArea[] TopLevelAreas = { StorageArea.Library, StorageArea.Temp, StorageArea.Logs, StorageArea.Obj, StorageArea.ServerBuild, StorageArea.DevClone };

    public static string LibraryPath(BasisInstall install) => Path.Combine(install.UnityProjectPath, "Library");
    public static string TempPath(BasisInstall install) => Path.Combine(install.UnityProjectPath, "Temp");
    public static string LogsPath(BasisInstall install) => Path.Combine(install.UnityProjectPath, "Logs");

    public Task<ProjectStorageReport> MeasureAsync(BasisInstall install, IProgress<StorageProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Measure(install, progress, ct), ct);

    public static ProjectStorageReport Measure(BasisInstall install, IProgress<StorageProgress>? progress = null, CancellationToken ct = default)
    {
        var comparer = StringComparer.FromComparison(Platform.PathComparison());
        var unity = Normalize(install.UnityProjectPath);
        var repo = Normalize(install.RepoRoot);
        var library = Path.Combine(unity, "Library");
        var roots = new Dictionary<string, StorageArea>(comparer)
        {
            [library] = StorageArea.Library,
            [Path.Combine(library, "PackageCache")] = StorageArea.PackageCache,
            [Path.Combine(unity, "Temp")] = StorageArea.Temp,
            [Path.Combine(unity, "Logs")] = StorageArea.Logs,
            [Path.Combine(unity, "obj")] = StorageArea.Obj,
        };
        var devRoot = Normalize(BasisDevStore.Root(unity));
        var server = Path.Combine(repo, ServerFolderName);
        var serverBuild = new Bucket(StorageArea.ServerBuild, server, null);
        var buckets = new List<Bucket>();
        long totalBytes = 0, totalFiles = 0, nextCheck = ProgressFileStep;
        var clock = Stopwatch.StartNew();
        var pending = new Stack<(string Folder, Bucket[] Buckets, bool InServer)>();
        pending.Push((repo, Array.Empty<Bucket>(), false));
        if (!IsInside(unity, repo)) pending.Push((unity, Array.Empty<Bucket>(), false));
        while (pending.Count > 0)
        {
            var (folder, active, inServer) = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Listing); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { continue; }
            try
            {
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entry is FileInfo file)
                    {
                        long length;
                        try { length = file.Length; }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                        totalBytes += length;
                        totalFiles++;
                        foreach (var bucket in active)
                        {
                            bucket.Bytes += length;
                            bucket.Files++;
                        }
                        if (totalFiles >= nextCheck)
                        {
                            nextCheck = totalFiles + ProgressFileStep;
                            if (clock.Elapsed >= ProgressInterval)
                            {
                                clock.Restart();
                                progress?.Report(new StorageProgress(totalFiles, totalBytes));
                            }
                        }
                        continue;
                    }
                    if (entry is not DirectoryInfo directory || directory.LinkTarget is not null) continue;
                    var path = directory.FullName;
                    var childBuckets = active;
                    if (roots.TryGetValue(path, out var area)) childBuckets = Add(active, buckets, new Bucket(area, path, null));
                    else if (comparer.Equals(folder, devRoot)) childBuckets = Add(active, buckets, new Bucket(StorageArea.DevClone, path, directory.Name));
                    else if (inServer && (directory.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) || directory.Name.Equals("obj", StringComparison.OrdinalIgnoreCase)) && !active.Contains(serverBuild))
                        childBuckets = Add(active, buckets, serverBuild);
                    pending.Push((path, childBuckets, inServer || comparer.Equals(path, server)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Measuring {folder}", ex); }
        }
        progress?.Report(new StorageProgress(totalFiles, totalBytes));
        var entriesOut = buckets.Distinct()
            .Select(b => new StorageEntry(b.Area, b.Path, b.Bytes, b.Files, b.Name))
            .OrderBy(e => e.Area)
            .ThenByDescending(e => e.Bytes)
            .ToList();
        var counted = entriesOut.Where(e => TopLevelAreas.Contains(e.Area)).Sum(e => e.Bytes);
        var countedFiles = entriesOut.Where(e => TopLevelAreas.Contains(e.Area)).Sum(e => e.Files);
        entriesOut.Add(new StorageEntry(StorageArea.Other, repo, Math.Max(0, totalBytes - counted), Math.Max(0, totalFiles - countedFiles)));
        return new ProjectStorageReport(repo, unity, totalBytes, totalFiles, entriesOut);
    }

    public static Task<StorageCleanResult> CleanLibraryAsync(BasisInstall install) =>
        CleanFolderAsync(install, LibraryPath(install), keepFolder: false, refuseWhileOpen: true, "Library");

    public static Task<StorageCleanResult> ClearTempAsync(BasisInstall install) =>
        CleanFolderAsync(install, TempPath(install), keepFolder: false, refuseWhileOpen: true, "Temp");

    public static Task<StorageCleanResult> ClearLogsAsync(BasisInstall install) =>
        CleanFolderAsync(install, LogsPath(install), keepFolder: true, refuseWhileOpen: false, "Logs");

    public async Task<IReadOnlyList<UnusedClone>> FindUnusedClonesAsync(BasisInstall install, CancellationToken ct = default)
    {
        var report = await basisDev.ScanAsync(install, compare: false, ct).ConfigureAwait(false);
        var result = new List<UnusedClone>();
        foreach (var package in report.Packages)
        {
            if (!package.CloneExists || package.CloneActive || package.CloneFolder is not { } folder || !BasisDevStore.IsCloneFolder(install.UnityProjectPath, folder)) continue;
            if (package.Listed is not null)
            {
                result.Add(new UnusedClone(package.Id, folder, false, UnusedCloneReason.Listed));
                continue;
            }
            bool busy;
            try { busy = await mounts.HasLocalWorkAsync(folder, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                DiagnosticLog.Write($"Checking {folder} for local work", ex);
                busy = true;
            }
            result.Add(new UnusedClone(package.Id, folder, !busy, busy ? UnusedCloneReason.LocalWork : null));
        }
        return result.OrderBy(c => c.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<StorageCleanResult> RemoveUnusedClonesAsync(BasisInstall install, IReadOnlyCollection<string>? ids = null, CancellationToken ct = default)
    {
        var candidates = await FindUnusedClonesAsync(install, ct).ConfigureAwait(false);
        var removed = new List<string>();
        var skipped = new List<string>();
        var failures = 0;
        long freed = 0;
        foreach (var clone in candidates)
        {
            if (ids is not null && !ids.Contains(clone.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (!clone.Removable)
            {
                skipped.Add($"{clone.Id}: {Describe(clone.Reason)}");
                continue;
            }
            var size = await Task.Run(() => FolderBytes(clone.Folder), ct).ConfigureAwait(false);
            var result = await basisDev.ApplyAsync(install, clone.Id, BasisDevAction.Release, force: false, ct: ct).ConfigureAwait(false);
            if (result.Ok)
            {
                removed.Add(clone.Id);
                freed += size;
            }
            else
            {
                skipped.Add($"{clone.Id}: {result.Message}");
                failures++;
            }
        }
        var message = removed.Count == 0 && skipped.Count == 0
            ? "There are no unused clones to remove."
            : $"Removed {removed.Count} unused clone(s) and freed {DiskSpace.Human(freed)}." + (skipped.Count > 0 ? " Kept: " + string.Join("; ", skipped) : "");
        return new StorageCleanResult(failures == 0, message, freed, removed, skipped);
    }

    public static string Describe(UnusedCloneReason? reason) => reason switch
    {
        UnusedCloneReason.Listed => "it is listed in Packages/" + BasisPackagesStore.FileName,
        UnusedCloneReason.LocalWork => "it has uncommitted or unpushed work",
        _ => "it isn't used",
    };

    public static long FolderBytes(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        long total = 0;
        try
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try { total += file.Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Measuring {folder}", ex); }
        return total;
    }

    private static async Task<StorageCleanResult> CleanFolderAsync(BasisInstall install, string folder, bool keepFolder, bool refuseWhileOpen, string label)
    {
        if (refuseWhileOpen && UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
            return StorageCleanResult.Fail($"Unity has {install.DisplayName} open. Close Unity, then clean {label} again.");
        if (!Directory.Exists(folder))
            return new StorageCleanResult(true, $"{label} is already empty.", 0, Array.Empty<string>(), Array.Empty<string>());
        var (freed, failed) = await Task.Run(() => DeleteTree(folder, keepFolder)).ConfigureAwait(false);
        if (failed.Count == 0)
            return new StorageCleanResult(true, $"Cleaned {label} and freed {DiskSpace.Human(freed)}.", freed, new[] { folder }, Array.Empty<string>());
        return new StorageCleanResult(false, $"Freed {DiskSpace.Human(freed)} from {label}, but {failed.Count} file(s) are in use and were left: {Path.GetFileName(failed[0])}" + (failed.Count > 1 ? " and others." : "."), freed, Array.Empty<string>(), failed);
    }

    private static (long Freed, List<string> Failed) DeleteTree(string root, bool keepRoot)
    {
        long freed = 0;
        var failed = new List<string>();
        var folders = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            folders.Add(folder);
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(folder).EnumerateFileSystemInfos("*", Listing).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add(folder);
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo directory)
                {
                    if (directory.LinkTarget is null)
                    {
                        pending.Push(directory.FullName);
                        continue;
                    }
                    try { directory.Delete(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(directory.FullName); }
                    continue;
                }
                var file = (FileInfo)entry;
                try
                {
                    var length = file.Length;
                    if (file.IsReadOnly) file.IsReadOnly = false;
                    file.Delete();
                    freed += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed.Add(file.FullName); }
            }
        }
        for (var i = folders.Count - 1; i >= (keepRoot ? 1 : 0); i--)
        {
            try { Directory.Delete(folders[i]); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return (freed, failed);
    }

    private static Bucket[] Add(Bucket[] active, List<Bucket> all, Bucket bucket)
    {
        if (!all.Contains(bucket)) all.Add(bucket);
        var next = new Bucket[active.Length + 1];
        active.CopyTo(next, 0);
        next[^1] = bucket;
        return next;
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string path, string folder)
    {
        var inner = Normalize(path);
        var outer = Normalize(folder);
        return string.Equals(inner, outer, Platform.PathComparison()) || inner.StartsWith(outer + Path.DirectorySeparatorChar, Platform.PathComparison());
    }

    private sealed class Bucket(StorageArea area, string path, string? name)
    {
        public StorageArea Area { get; } = area;
        public string Path { get; } = path;
        public string? Name { get; } = name;
        public long Bytes;
        public long Files;
    }
}
