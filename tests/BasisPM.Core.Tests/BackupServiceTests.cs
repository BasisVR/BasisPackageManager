using System.IO.Compression;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BackupServiceTests
{
    private static string MakeProject(TempDir t)
    {
        var root = t.CreateDir("Proj");
        t.WriteFile("Proj/Assets/a.txt", "asset");
        t.WriteFile("Proj/Packages/manifest.json", "{}");
        t.WriteFile("Proj/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.25f1");
        t.WriteFile("Proj/UserSettings/u.txt", "user");
        t.WriteFile("Proj/Library/junk.bin", "cache");
        t.WriteFile("Proj/Temp/t.tmp", "temp");
        return root;
    }

    [Fact]
    public void LooksLikeUnityProject_requires_assets_and_project_settings()
    {
        using var t = new TempDir();
        var root = MakeProject(t);
        Assert.True(BackupService.LooksLikeUnityProject(root));
        Assert.False(BackupService.LooksLikeUnityProject(t.Combine("nope")));
        Assert.False(BackupService.LooksLikeUnityProject(""));
    }

    [Fact]
    public async Task CreateBackup_zips_only_the_backup_worthy_folders()
    {
        using var t = new TempDir();
        var root = MakeProject(t);
        var dest = t.CreateDir("backups");

        var zipPath = await BackupService.CreateBackupAsync(root, dest, "20260704-120000");

        Assert.True(File.Exists(zipPath));
        Assert.Equal("Proj-backup-20260704-120000.zip", Path.GetFileName(zipPath));

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToList();

        Assert.Contains("Assets/a.txt", entries);
        Assert.Contains("Packages/manifest.json", entries);
        Assert.Contains("ProjectSettings/ProjectVersion.txt", entries);
        Assert.Contains("UserSettings/u.txt", entries);
        Assert.DoesNotContain(entries, e => e.StartsWith("Library/"));
        Assert.DoesNotContain(entries, e => e.StartsWith("Temp/"));
    }

    [Fact]
    public async Task CreateBackup_reports_progress_per_folder()
    {
        using var t = new TempDir();
        var root = MakeProject(t);
        var dest = t.CreateDir("backups");
        var messages = new List<string>();

        await BackupService.CreateBackupAsync(root, dest, "ts", messages.Add);

        Assert.Contains(messages, m => m.Contains("Assets"));
    }

    [Fact]
    public async Task CreateBackup_throws_for_non_project()
    {
        using var t = new TempDir();
        var notProject = t.CreateDir("plain");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => BackupService.CreateBackupAsync(notProject, t.Combine("out"), "ts"));
    }
}
