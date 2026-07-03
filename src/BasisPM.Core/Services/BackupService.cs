using System.IO.Compression;

namespace BasisPM.Core.Services;

/// <summary>
/// Backs up a Unity project by zipping the folders that can't be regenerated —
/// <c>Assets</c>, <c>Packages</c>, <c>ProjectSettings</c>, <c>UserSettings</c>. Everything else
/// (Library, Temp, obj, Logs, Build) is a cache Unity rebuilds on open, so it's intentionally
/// excluded (that's what keeps a backup small).
/// </summary>
public sealed class BackupService
{
    private static readonly string[] BackupFolders = { "Assets", "Packages", "ProjectSettings", "UserSettings" };

    /// <summary>A folder is a Unity project if it has both Assets/ and ProjectSettings/.</summary>
    public static bool LooksLikeUnityProject(string projectPath) =>
        !string.IsNullOrWhiteSpace(projectPath) &&
        Directory.Exists(Path.Combine(projectPath, "Assets")) &&
        Directory.Exists(Path.Combine(projectPath, "ProjectSettings"));

    /// <summary>
    /// Zips the backup-worthy folders of <paramref name="projectPath"/> into
    /// <paramref name="destDir"/> and returns the created .zip path. <paramref name="timestamp"/>
    /// names the file (pass e.g. <c>DateTime.Now.ToString("yyyyMMdd-HHmmss")</c>).
    /// </summary>
    public static async Task<string> CreateBackupAsync(
        string projectPath, string destDir, string timestamp,
        Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!LooksLikeUnityProject(projectPath))
            throw new InvalidOperationException("That folder is not a Unity project (no Assets/ + ProjectSettings/).");

        Directory.CreateDirectory(destDir);
        var name = new DirectoryInfo(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        var zipPath = Path.Combine(destDir, $"{name}-backup-{timestamp}.zip");

        await Task.Run(() =>
        {
            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var folder in BackupFolders)
            {
                var src = Path.Combine(projectPath, folder);
                if (!Directory.Exists(src)) continue;
                onProgress?.Invoke($"Backing up {folder}…");
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(projectPath, file).Replace('\\', '/');
                    try { zip.CreateEntryFromFile(file, rel, CompressionLevel.Fastest); }
                    catch (IOException) { /* skip files locked by the editor */ }
                }
            }
        }, ct).ConfigureAwait(false);

        return zipPath;
    }
}
