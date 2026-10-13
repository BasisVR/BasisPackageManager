using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class FileRetryTests
{
    private static Task ReleaseLater(FileStream held) => Task.Run(async () =>
    {
        await Task.Delay(300);
        held.Dispose();
    });

    [WindowsFact]
    public async Task A_delete_waits_for_a_file_that_is_only_briefly_in_use()
    {
        using var t = new TempDir();
        var folder = t.CreateDir("clone");
        var file = t.WriteFile("clone/Plugins/native.dll", "x");
        t.WriteFile("clone/.git/HEAD", "ref: refs/heads/main\n");
        File.SetAttributes(t.Combine("clone/.git/HEAD"), FileAttributes.ReadOnly);
        var release = ReleaseLater(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));

        FileRetry.DeleteDirectory(folder);
        await release;

        Assert.False(Directory.Exists(folder));
    }

    [WindowsFact]
    public void A_file_that_stays_in_use_still_fails_after_the_last_try()
    {
        using var t = new TempDir();
        var file = t.WriteFile("clone/a.txt", "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var tries = 0;

        var error = Record.Exception(() => FileRetry.Run(() => { tries++; File.Delete(file); }, new[] { TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10) }));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(3, tries);
    }

    [Fact]
    public void Only_file_system_errors_are_retried()
    {
        var tries = 0;

        Assert.Throws<InvalidOperationException>(() => FileRetry.Run(() => { tries++; throw new InvalidOperationException(); }, new[] { TimeSpan.Zero }));
        Assert.Equal(1, tries);
    }

    [Fact]
    public void Keeping_the_folder_empties_it_instead()
    {
        using var t = new TempDir();
        var folder = t.CreateDir("dest");
        t.WriteFile("dest/a.txt", "x");
        t.WriteFile("dest/sub/b.txt", "y");

        FileRetry.DeleteDirectory(folder, keepFolder: true);

        Assert.True(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
    }

    [WindowsFact]
    public async Task Deleting_a_project_waits_for_a_briefly_held_file()
    {
        using var t = new TempDir();
        var root = t.CreateDir("Project");
        var file = t.WriteFile("Project/Library/cache.bin", "x");
        var release = ReleaseLater(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read));

        await BasisInstallService.DeleteFolderAsync(root);
        await release;

        Assert.False(Directory.Exists(root));
    }

    [WindowsFact]
    public async Task Swap_back_finishes_once_a_briefly_held_file_is_let_go()
    {
        using var t = new TempDir();
        t.CreateDir("Basis/Assets");
        var project = t.Combine("Basis");
        t.WriteFile("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
        var manifest = t.WriteFile("Basis/Packages/manifest.json", """{ "dependencies": {} }""");
        t.WriteFile("Basis/Packages/com.x/package.json", """{ "name": "com.x" }""");
        var dll = t.WriteFile("Basis/Packages/com.x/Plugins/native.dll", "x");
        t.WriteFile("Basis/Packages/com.x/.git/HEAD", "ref: refs/heads/main\n");
        var registry = new MountRegistry(t.Combine("data"));
        registry.Add(new MountRecord(project, "com.x", t.Combine("Basis/Packages/com.x"), "https://github.com/o/r.git"));
        var service = new MountService(new GitService(), new UnityProjectService(), registry);
        var install = new BasisInstall { RepoRoot = t.Path, UnityProjectPath = project, Name = "Basis" };
        var release = ReleaseLater(new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

        var result = await service.UnmountAsync(install, "com.x", restoreManifest: true);
        await release;

        Assert.True(result.Ok, result.Error);
        Assert.False(Directory.Exists(t.Combine("Basis/Packages/com.x")));
        Assert.Contains("https://github.com/o/r.git", File.ReadAllText(manifest));
    }
}
