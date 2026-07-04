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
        if (parsed.Path is not null)
            return MountResult.Fail("This package lives in a subfolder of its repo — subfolder mounting isn't supported yet.");

        var dest = Path.Combine(install.UnityProjectPath, "Packages", packageId);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            return MountResult.Fail($"{dest} already exists and isn't empty.");

        var clone = await _git.CloneAsync(parsed.CloneUrl, dest, parsed.Ref, onProgress, ct).ConfigureAwait(false);
        if (!clone.Ok) return MountResult.Fail($"Clone failed: {clone.Output}");

        var info = await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false);
        info.Manifest.Dependencies.Remove(packageId);
        await _projects.SaveManifestAsync(install.UnityProjectPath, info.Manifest, ct).ConfigureAwait(false);
        install.Manifest = info.Manifest;

        _registry.Add(new MountRecord(install.UnityProjectPath, packageId, dest, manifestGitValue));
        return MountResult.Success(dest);
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
        return MountResult.Success(dest);
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
