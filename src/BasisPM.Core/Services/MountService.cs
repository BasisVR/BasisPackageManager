using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record MountResult(bool Ok, string? FolderPath, string? Error)
{
    public static MountResult Success(string folderPath) => new(true, folderPath, null);
    public static MountResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// Mounts a git-loaded package into a project's <c>Packages/</c> folder for editing, and swaps it back.
/// Mount = clone the repo into <c>Packages/&lt;id&gt;/</c> and drop its <c>manifest.json</c> git line so Unity
/// uses the local copy. Swap-back = restore the manifest line and delete the folder.
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
        if (BasisDevStore.Read(unityProjectPath, packageId) is { } sidecar && BasisDevStore.ResolveFolder(unityProjectPath, sidecar) is { } folder)
            return new MountRecord(unityProjectPath, packageId, folder, sidecar.Manifest.Original ?? _registry.Find(unityProjectPath, packageId)?.OriginalManifestValue ?? "");
        return _registry.Find(unityProjectPath, packageId);
    }

    public IReadOnlyList<MountRecord> ListMounts(string unityProjectPath)
    {
        var mounts = new Dictionary<string, MountRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in _registry.ForInstall(unityProjectPath)) mounts[record.PackageId] = record;
        foreach (var sidecar in BasisDevStore.List(unityProjectPath))
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
            BasisDevStore.Write(install.UnityProjectPath, new BasisDevSidecar
            {
                Package = packageId,
                Folder = BasisDevStore.RelativeFolder(install.UnityProjectPath, folder),
                Upstream = new BasisDevUpstream
                {
                    Url = parsed?.CloneUrl ?? UpmGitUrl.Parse(origin)?.CloneUrl ?? origin ?? "",
                    Ref = parsed?.Ref,
                    Path = parsed?.Path,
                    Commit = await _git.ResolveCommitAsync(folder, "HEAD").ConfigureAwait(false),
                },
                Manifest = new BasisDevManifestLine { Original = string.IsNullOrWhiteSpace(originalManifestValue) ? null : originalManifestValue, Mounted = mountedManifestValue },
                Recorded = DateTimeOffset.UtcNow,
                Tool = BasisDevStore.ToolName,
            });
            GitExclude.Add(install.RepoRoot, BasisDevStore.Root(install.UnityProjectPath), _git.GetCommonGitDir);
        }
        catch (Exception ex) { DiagnosticLog.Write($"Recording the {BasisDevStore.FolderName} sidecar for {packageId}", ex); }
    }

    public async Task<MountResult> MountAsync(BasisInstall install, string packageId, string manifestGitValue, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var parsed = UpmGitUrl.Parse(manifestGitValue);
        if (parsed is null) return MountResult.Fail("Couldn't parse this package's git URL.");
        if (!GitUrlPolicy.IsSafeUrl(parsed.CloneUrl))
            return MountResult.Fail("This package's git URL uses an unsupported or unsafe transport.");
        if (!GitUrlPolicy.IsSafeRef(parsed.Ref))
            return MountResult.Fail("This package pins an invalid git ref.");
        if (!GitUrlPolicy.IsSafeSubPath(parsed.Path))
            return MountResult.Fail("This package's sub-path escapes the repository.");

        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);

        if (parsed.Path is null)
        {
            // Root-level package: clone straight into Packages/<id> as an embedded package.
            var dest = Path.Combine(install.UnityProjectPath, "Packages", packageId);
            if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
                return MountResult.Fail($"{dest} already exists and isn't empty.");

            var clone = await _git.CloneAtAsync(parsed.CloneUrl, dest, parsed.Ref, onProgress, ct).ConfigureAwait(false);
            if (!clone.Ok) { TryForceDelete(dest); return MountResult.Fail($"Clone failed: {clone.Output}"); }

            try
            {
                info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
                info.Manifest.Dependencies.Remove(packageId);
                await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
            }
            catch { TryForceDelete(dest); throw; }
            install.Manifest = info.Manifest;

            _registry.Add(new MountRecord(install.UnityProjectPath, packageId, dest, manifestGitValue));
            GitExclude.Add(install.RepoRoot, dest, _git.GetCommonGitDir);
            await RecordAsync(install, packageId, dest, manifestGitValue, null).ConfigureAwait(false);
            return MountResult.Success(dest);
        }

        // Subfolder package: clone the whole repo into a Unity-ignored workspace (.basisdev/<id>) and
        // point the manifest at the sub-path with a Unity "file:" local dependency (editable in place).
        // The full clone is what the PR flow commits and pushes from.
        var workspace = Path.Combine(install.UnityProjectPath, ".basisdev", packageId);
        if (Directory.Exists(workspace) && Directory.EnumerateFileSystemEntries(workspace).Any())
            return MountResult.Fail($"{workspace} already exists and isn't empty.");

        var repoClone = await _git.CloneAtAsync(parsed.CloneUrl, workspace, parsed.Ref, onProgress, ct).ConfigureAwait(false);
        if (!repoClone.Ok) { TryForceDelete(workspace); return MountResult.Fail($"Clone failed: {repoClone.Output}"); }

        var pkgDir = Path.Combine(workspace, parsed.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(Path.Combine(pkgDir, "package.json")))
        {
            TryForceDelete(workspace);
            return MountResult.Fail($"Couldn't find package.json at “{parsed.Path}” in {parsed.Slug}.");
        }

        var packagesDir = Path.Combine(install.UnityProjectPath, "Packages");
        var relative = Path.GetRelativePath(packagesDir, pkgDir).Replace('\\', '/');
        try
        {
            info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
            info.Manifest.Dependencies[packageId] = "file:" + relative;
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        }
        catch { TryForceDelete(workspace); throw; }
        install.Manifest = info.Manifest;

        _registry.Add(new MountRecord(install.UnityProjectPath, packageId, workspace, manifestGitValue));
        GitExclude.Add(install.RepoRoot, workspace, _git.GetCommonGitDir);
        await RecordAsync(install, packageId, workspace, manifestGitValue, "file:" + relative).ConfigureAwait(false);
        return MountResult.Success(workspace);
    }

    public Task<MountResult> SwapBackAsync(BasisInstall install, string packageId, CancellationToken ct = default) =>
        UnmountAsync(install, packageId, restoreManifest: true, ct: ct);

    public async Task<MountResult> UnmountAsync(BasisInstall install, string packageId, bool restoreManifest, string? folder = null, string? restoreLine = null, CancellationToken ct = default)
    {
        var record = FindMount(install.UnityProjectPath, packageId);
        var dest = folder ?? record?.FolderPath ?? Path.Combine(install.UnityProjectPath, "Packages", packageId);
        if (Directory.Exists(dest) && !IsWorkingClone(dest))
            return MountResult.Fail($"{dest} isn't a mounted git clone, so it was left untouched.");

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
            return MountResult.Fail(restoreManifest ? $"Restored the manifest, but couldn't delete {dest}: {ex.Message}" : $"Couldn't delete {dest}: {ex.Message}");
        }

        Forget(install, packageId, dest);
        return MountResult.Success(dest);
    }

    public void Forget(BasisInstall install, string packageId, string? folder)
    {
        _registry.Remove(install.UnityProjectPath, packageId);
        try { BasisDevStore.Delete(install.UnityProjectPath, packageId); }
        catch (Exception ex) { DiagnosticLog.Write($"Deleting the {BasisDevStore.FolderName} sidecar for {packageId}", ex); }
        if (folder is not null) GitExclude.Remove(install.RepoRoot, folder, _git.GetCommonGitDir);
    }

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
        Directory.Delete(path, recursive: true);
    }
}
