using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record MountResult(bool Ok, string? FolderPath, string? Error)
{
    public string? ManifestValue { get; init; }
    public bool NotAPackage { get; init; }
    public static MountResult Success(string folderPath, string? manifestValue = null) => new(true, folderPath, null) { ManifestValue = manifestValue };
    public static MountResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Mounts a git-loaded package into a project's <c>.basisdev/</c> folder for editing, and swaps it back.
/// Mount = clone the repo into <c>.basisdev/&lt;id&gt;/</c>, point its <c>manifest.json</c> line at the clone with a
/// <c>file:</c> path and list it in <c>Packages/basispackages.json</c>. Swap-back = restore the manifest line and delete the folder.
/// </summary>
public sealed class MountService
{
    private readonly GitService _git;
    private readonly UnityProjectService _projects;
    private readonly MountRegistry _registry;

    public MountService(GitService git, UnityProjectService projects, MountRegistry registry)
    {
        _git = git;
        _projects = projects;
        _registry = registry;
    }

    public static bool IsWorkingClone(string? folder) => !string.IsNullOrEmpty(folder) && Directory.Exists(Path.Combine(folder, ".git"));

    public MountRecord? FindMount(string unityProjectPath, string packageId)
    {
        var sidecar = BasisDevStore.Read(unityProjectPath, packageId) ?? BasisPackagesStore.SidecarFor(unityProjectPath, packageId);
        if (sidecar is not null && BasisDevStore.ResolveFolder(unityProjectPath, sidecar) is { } folder)
            return new MountRecord(unityProjectPath, packageId, folder, sidecar.Manifest.Original ?? _registry.Find(unityProjectPath, packageId)?.OriginalManifestValue ?? "");
        return _registry.Find(unityProjectPath, packageId);
    }

    public IReadOnlyList<MountRecord> ListMounts(string unityProjectPath)
    {
        var mounts = new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _registry.ForInstall(unityProjectPath)) mounts[record.PackageId] = record;
        foreach (var sidecar in BasisPackagesStore.Sidecars(unityProjectPath).Concat(BasisDevStore.List(unityProjectPath)))
        {
            if (BasisDevStore.ResolveFolder(unityProjectPath, sidecar) is not { } folder) continue;
            mounts.TryGetValue(sidecar.Package, out var record);
            mounts[sidecar.Package] = new MountRecord(unityProjectPath, sidecar.Package, folder, sidecar.Manifest.Original ?? record?.OriginalManifestValue ?? "");
        }
        return mounts.Values.OrderBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<bool> HasLocalWorkAsync(string folder, CancellationToken ct = default)
    {
        if (!IsWorkingClone(folder)) return false;
        var status = await _git.GetStatusAsync(folder, ct).ConfigureAwait(false);
        return status.ChangeCount > 0 || await _git.HasUnpublishedWorkAsync(folder, ct).ConfigureAwait(false);
    }

    public async Task RecordAsync(BasisInstall install, string packageId, string folder, string originalManifestValue, string? mountedManifestValue)
    {
        try
        {
            var parsed = UpmGitUrl.Parse(originalManifestValue);
            var origin = parsed is null ? await _git.GetRemoteUrlAsync(folder, "origin").ConfigureAwait(false) : null;
            var upstream = new BasisDevUpstream
            {
                Url = parsed?.CloneUrl ?? UpmGitUrl.Parse(origin)?.CloneUrl ?? origin ?? "",
                Ref = parsed?.Ref,
                Path = parsed?.Path,
                Commit = await _git.ResolveCommitAsync(folder, "HEAD").ConfigureAwait(false),
            };
            BasisDevStore.Write(install.UnityProjectPath, new BasisDevSidecar
            {
                Package = packageId,
                Folder = BasisDevStore.RelativeFolder(install.UnityProjectPath, folder),
                Upstream = upstream,
                Manifest = new BasisDevManifestLine { Original = string.IsNullOrWhiteSpace(originalManifestValue) ? null : originalManifestValue, Mounted = mountedManifestValue },
                Recorded = DateTimeOffset.UtcNow,
                Tool = BasisDevStore.ToolName,
            });
            GitExclude.Add(install.RepoRoot, BasisDevStore.Root(install.UnityProjectPath), _git.GetCommonGitDir);
            ListClone(install, packageId, folder, upstream);
        }
        catch (Exception ex) { DiagnosticLog.Write($"Recording the {BasisDevStore.FolderName} sidecar for {packageId}", ex); }
    }

    public static void ListClone(BasisInstall install, string packageId, string folder, BasisDevUpstream upstream)
    {
        if (!BasisDevStore.IsCloneFolder(install.UnityProjectPath, folder) || string.IsNullOrWhiteSpace(upstream.Url)) return;
        try { BasisPackagesStore.Set(install.UnityProjectPath, packageId, BasisPackagesStore.EntryFor(upstream, BasisDevStore.PackageRoot(folder, upstream.Path))); }
        catch (Exception ex) { DiagnosticLog.Write($"Listing {packageId} in {BasisPackagesStore.FileName}", ex); }
    }

    public async Task<MountResult> MountAsync(BasisInstall install, string packageId, string manifestGitValue, Action<string>? onProgress = null, CancellationToken ct = default, bool orDefaultBranch = false)
    {
        var parsed = UpmGitUrl.Parse(manifestGitValue);
        if (parsed is null) return MountResult.Fail("Couldn't parse this package's git URL.");
        if (!GitUrlPolicy.IsSafeUrl(parsed.CloneUrl))
            return MountResult.Fail("This package's git URL uses an unsupported or unsafe transport.");
        if (!GitUrlPolicy.IsSafeRef(parsed.Ref))
            return MountResult.Fail("This package pins an invalid git ref.");
        if (!GitUrlPolicy.IsSafeSubPath(parsed.Path))
            return MountResult.Fail("This package's sub-path escapes the repository.");
        if (!BasisDevStore.IsValidId(packageId))
            return MountResult.Fail($"\"{packageId}\" can't be used as a folder name.");

        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);

        // Clone the whole repo into the Unity-ignored workspace (.basisdev/<id>) and point the manifest at the package
        // folder with a Unity "file:" local dependency (editable in place). The full clone is what the PR flow commits and pushes from.
        var folder = BasisDevStore.CloneFolder(install.UnityProjectPath, packageId);
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            return MountResult.Fail($"{folder} already exists and isn't empty.");

        var clone = await _git.CloneAtAsync(parsed.CloneUrl, folder, parsed.Ref, onProgress, ct).ConfigureAwait(false);
        if (!clone.Ok) { TryForceDelete(folder); return MountResult.Fail($"Clone failed: {clone.Output}"); }

        var mountedValue = await PackageCheckoutAsync(folder, manifestGitValue, orDefaultBranch, ct).ConfigureAwait(false);
        if (mountedValue is null || BasisDevStore.PackageRoot(folder, parsed.Path) is not { } packageRoot)
        {
            TryForceDelete(folder);
            return MountResult.Fail(NotAPackageMessage(parsed, orDefaultBranch)) with { NotAPackage = true };
        }

        var mountedLine = BasisDevStore.ManifestLine(install.UnityProjectPath, packageRoot);
        try
        {
            info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
            info.Manifest.Dependencies[packageId] = mountedLine;
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        }
        catch { TryForceDelete(folder); throw; }
        install.Manifest = info.Manifest;

        _registry.Add(new MountRecord(install.UnityProjectPath, packageId, folder, mountedValue));
        GitExclude.Add(install.RepoRoot, BasisDevStore.Root(install.UnityProjectPath), _git.GetCommonGitDir);
        await RecordAsync(install, packageId, folder, mountedValue, mountedLine).ConfigureAwait(false);
        return MountResult.Success(folder, mountedValue);
    }

    public async Task<string?> PackageCheckoutAsync(string folder, string manifestGitValue, bool orDefaultBranch, CancellationToken ct = default)
    {
        if (UpmGitUrl.Parse(manifestGitValue) is not { } parsed) return null;
        if (HasPackageJson(folder, parsed.Path)) return manifestGitValue;
        if (!orDefaultBranch || parsed.Ref is null) return null;
        var heads = (await _git.ListRefsAsync(folder, new[] { "refs/heads" }, 10, ct).ConfigureAwait(false))
            .Select(head => head["refs/heads/".Length..])
            .Where(head => !string.Equals(head, parsed.Ref, StringComparison.Ordinal))
            .ToList();
        if (heads.Count != 1 || !(await _git.CheckoutAsync(folder, heads[0], ct).ConfigureAwait(false)).Ok) return null;
        return HasPackageJson(folder, parsed.Path) ? parsed.ToManifestUrl(null, parsed.Path) : null;
    }

    private static bool HasPackageJson(string folder, string? subPath) =>
        File.Exists(Path.Combine(subPath is null ? folder : Path.Combine(folder, subPath.Replace('/', Path.DirectorySeparatorChar)), "package.json"));

    private static string NotAPackageMessage(UpmGitUrl parsed, bool triedDefaultBranch)
    {
        var where = parsed.Path is null ? "package.json" : $"package.json at “{parsed.Path}”";
        var at = parsed.Ref is null ? "its default branch"
            : triedDefaultBranch ? $"{parsed.Ref} or its default branch"
            : parsed.Ref;
        return $"{parsed.Slug} has no {where} at {at}, so Unity can't load it as a package.";
    }

    public Task<MountResult> SwapBackAsync(BasisInstall install, string packageId, CancellationToken ct = default) =>
        UnmountAsync(install, packageId, restoreManifest: true, ct: ct);

    public async Task<MountResult> UnmountAsync(BasisInstall install, string packageId, bool restoreManifest, string? folder = null, string? restoreLine = null, CancellationToken ct = default)
    {
        var record = FindMount(install.UnityProjectPath, packageId);
        var dest = folder ?? record?.FolderPath ?? Path.Combine(install.UnityProjectPath, "Packages", packageId);
        if (Directory.Exists(dest) && !IsWorkingClone(dest))
            return MountResult.Fail($"{dest} isn't a mounted git clone, so it was left untouched.");
        if (await Task.Run(() => DeleteBlocker(install.UnityProjectPath, dest), ct).ConfigureAwait(false) is { } blocker)
            return MountResult.Fail($"Couldn't delete {dest}: {blocker}");

        if (restoreManifest)
        {
            // Prefer the exact original line; otherwise reconstruct from the clone's origin.
            var restore = string.IsNullOrEmpty(restoreLine) ? record?.OriginalManifestValue : restoreLine;
            if (string.IsNullOrEmpty(restore) && Directory.Exists(dest))
            {
                var origin = await _git.GetRemoteUrlAsync(dest, "origin", ct).ConfigureAwait(false);
                restore = UpmGitUrl.Parse(origin)?.CloneUrl;
            }
            if (string.IsNullOrEmpty(restore))
                return MountResult.Fail("Couldn't determine the original git URL to restore.");

            var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
            info.Manifest.Dependencies[packageId] = restore;
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
            install.Manifest = info.Manifest;
        }

        try { if (Directory.Exists(dest)) ForceDeleteDirectory(dest); }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Deleting mounted package directory {dest}", ex);
            var reason = OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(install.UnityProjectPath) ? $"{ex.Message} Close Unity, then try again." : ex.Message;
            return MountResult.Fail(restoreManifest ? $"Restored the manifest, but couldn't delete {dest}: {reason}" : $"Couldn't delete {dest}: {reason}");
        }

        Forget(install, packageId, dest);
        return MountResult.Success(dest);
    }

    public void Forget(BasisInstall install, string packageId, string? folder)
    {
        _registry.Remove(install.UnityProjectPath, packageId);
        try { BasisDevStore.Delete(install.UnityProjectPath, packageId); }
        catch (Exception ex) { DiagnosticLog.Write($"Deleting the {BasisDevStore.FolderName} sidecar for {packageId}", ex); }
        try { BasisPackagesStore.Remove(install.UnityProjectPath, packageId); }
        catch (Exception ex) { DiagnosticLog.Write($"Removing {packageId} from {BasisPackagesStore.FileName}", ex); }
        if (folder is not null) GitExclude.Remove(install.RepoRoot, folder, _git.GetCommonGitDir);
    }

    public static string? DeleteBlocker(string unityProjectPath, string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder)) return null;
        var files = Directory.EnumerateFiles(folder).Concat(Directory.EnumerateDirectories(folder).Where(d => !IsGitDir(d)).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)));
        if (files.FirstOrDefault(IsInUse) is not { } busy) return null;
        var name = Path.GetFileName(busy);
        return UnityProjectService.IsOpenInUnity(unityProjectPath)
            ? $"Unity is using {name}. Close Unity, then try again."
            : $"{name} is in use by another program, usually Unity. Close it, then try again.";
    }

    private static bool IsInUse(string file)
    {
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private static bool IsGitDir(string path) => string.Equals(Path.GetFileName(path), ".git", StringComparison.OrdinalIgnoreCase);

    private static void TryForceDelete(string path)
    {
        try { if (Directory.Exists(path)) ForceDeleteDirectory(path); }
        catch (Exception ex) { DiagnosticLog.Write($"Cleaning up mount directory {path}", ex); }
    }

    // git stores read-only objects under .git; clear attributes before deleting.
    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); }
            catch (Exception ex) { DiagnosticLog.Write($"Clearing file attributes for {file}", ex); }
        }
        FileRetry.Run(() =>
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path).Where(e => !IsGitDir(e)).ToList())
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
        });
        FileRetry.Run(() => { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); });
    }
}
