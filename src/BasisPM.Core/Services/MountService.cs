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

            info.Manifest.Dependencies.Remove(packageId);
            await _projects.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
            install.Manifest = info.Manifest;

            _registry.Add(new MountRecord(install.UnityProjectPath, packageId, dest, manifestGitValue));
            GitExclude.Add(install.RepoRoot, dest, _git.GetCommonGitDir);
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
        info.Manifest.Dependencies[packageId] = "file:" + relative;
        await _projects.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        install.Manifest = info.Manifest;

        _registry.Add(new MountRecord(install.UnityProjectPath, packageId, workspace, manifestGitValue));
        GitExclude.Add(install.RepoRoot, workspace, _git.GetCommonGitDir);
        return MountResult.Success(workspace);
    }

    public async Task<MountResult> SwapBackAsync(BasisInstall install, string packageId, CancellationToken ct = default)
    {
        var record = _registry.Find(install.UnityProjectPath, packageId);
        var dest = record?.FolderPath ?? Path.Combine(install.UnityProjectPath, "Packages", packageId);

        // Prefer the exact original line; otherwise reconstruct from the clone's origin.
        var restore = record?.OriginalManifestValue;
        if (string.IsNullOrEmpty(restore) && Directory.Exists(dest))
        {
            var origin = await _git.GetRemoteUrlAsync(dest, "origin", ct).ConfigureAwait(false);
            restore = UpmGitUrl.Parse(origin)?.CloneUrl;
        }
        if (string.IsNullOrEmpty(restore))
            return MountResult.Fail("Couldn't determine the original git URL to restore.");

        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
        info.Manifest.Dependencies[packageId] = restore;
        await _projects.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        install.Manifest = info.Manifest;

        try { if (Directory.Exists(dest)) ForceDeleteDirectory(dest); }
        catch (Exception ex) { return MountResult.Fail($"Restored the manifest, but couldn't delete {dest}: {ex.Message}"); }

        _registry.Remove(install.UnityProjectPath, packageId);
        GitExclude.Remove(install.RepoRoot, dest, _git.GetCommonGitDir);
        return MountResult.Success(dest);
    }

    private static void TryForceDelete(string path)
    {
        try { if (Directory.Exists(path)) ForceDeleteDirectory(path); }
        catch { }
    }

    // git stores read-only objects under .git; clear attributes before deleting.
    private static void ForceDeleteDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }
}
