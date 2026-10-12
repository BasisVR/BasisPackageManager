using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class MountServiceTests
{
    private static MountService NewService(TempDir t)
        => new(new GitService(), new UnityProjectService(), new MountRegistry(t.Path));

    private static BasisInstall Install => new()
    {
        RepoRoot = @"C:\Install",
        UnityProjectPath = @"C:\Install",
        Name = "Install",
    };

    [Fact]
    public async Task Mount_rejects_an_unparseable_url()
    {
        using var t = new TempDir();
        var result = await NewService(t).MountAsync(Install, "com.x", "not a url at all");
        Assert.False(result.Ok);
        Assert.Contains("parse", result.Error);
    }

    [Fact]
    public async Task Mount_rejects_an_unsafe_git_ref()
    {
        using var t = new TempDir();
        var result = await NewService(t).MountAsync(Install, "com.x", "https://github.com/o/r.git#--upload-pack=evil");
        Assert.False(result.Ok);
        Assert.Contains("git ref", result.Error);
    }

    [Fact]
    public async Task Mount_rejects_a_traversal_subpath()
    {
        using var t = new TempDir();
        var result = await NewService(t).MountAsync(Install, "com.x", "https://github.com/o/r.git?path=../escape");
        Assert.False(result.Ok);
        Assert.Contains("sub-path", result.Error);
    }

    [Fact]
    public void IsWorkingClone_requires_a_git_directory_in_the_folder_itself()
    {
        using var t = new TempDir();
        Assert.False(MountService.IsWorkingClone(t.CreateDir("plain")));
        t.CreateDir("clone/.git");
        Assert.True(MountService.IsWorkingClone(t.Combine("clone")));
        Assert.False(MountService.IsWorkingClone(t.CreateDir("clone/inner")));
        Assert.False(MountService.IsWorkingClone(null));
    }

    [Fact]
    public async Task SwapBack_leaves_a_registered_folder_that_is_not_a_clone_untouched()
    {
        using var t = new TempDir();
        var project = t.CreateDir("Basis");
        var manifest = t.WriteFile("Basis/Packages/manifest.json", """{ "dependencies": {} }""");
        var package = t.WriteFile("Basis/Packages/com.x/package.json", """{ "name": "com.x" }""");
        var registry = new MountRegistry(t.Combine("data"));
        registry.Add(new MountRecord(project, "com.x", t.Combine("Basis/Packages/com.x"), "https://github.com/o/r.git"));
        var service = new MountService(new GitService(), new UnityProjectService(), registry);
        var install = new BasisInstall { RepoRoot = t.Path, UnityProjectPath = project, Name = "Basis" };

        var result = await service.SwapBackAsync(install, "com.x");

        Assert.False(result.Ok);
        Assert.True(File.Exists(package));
        Assert.DoesNotContain("com.x", File.ReadAllText(manifest));
    }

    [WindowsFact]
    public async Task SwapBack_changes_nothing_while_a_file_in_the_clone_is_in_use()
    {
        using var t = new TempDir();
        var (service, install, manifest) = MountedClone(t);
        var clone = t.Combine("Basis/Packages/com.x");

        using (new FileStream(t.Combine("Basis/Packages/com.x/Plugins/steam_api64.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await service.SwapBackAsync(install, "com.x");
            Assert.False(result.Ok);
            Assert.Equal($"Couldn't delete {clone}: steam_api64.dll is in use by another program, usually Unity. Close it, then try again.", result.Error);
        }

        Assert.True(MountService.IsWorkingClone(clone));
        Assert.True(File.Exists(t.Combine("Basis/Packages/com.x/package.json")));
        Assert.DoesNotContain("com.x", File.ReadAllText(manifest));
    }

    [WindowsFact]
    public void DeleteBlocker_tells_the_user_to_close_Unity_when_it_has_the_project_open()
    {
        using var t = new TempDir();
        var (_, install, _) = MountedClone(t);
        var lockFile = t.WriteFile("Basis/Temp/UnityLockfile", "");
        using var dll = new FileStream(t.Combine("Basis/Packages/com.x/Plugins/steam_api64.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var unity = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal("Unity is using steam_api64.dll. Close Unity, then try again.", MountService.DeleteBlocker(install.UnityProjectPath, t.Combine("Basis/Packages/com.x")));
    }

    [Fact]
    public void DeleteBlocker_ignores_free_files_and_anything_under_git()
    {
        using var t = new TempDir();
        var (_, install, _) = MountedClone(t);
        using var index = new FileStream(t.WriteFile("Basis/Packages/com.x/.git/index", "x"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Null(MountService.DeleteBlocker(install.UnityProjectPath, t.Combine("Basis/Packages/com.x")));
    }

    [WindowsFact]
    public async Task A_failed_delete_keeps_git_so_swap_back_can_run_again()
    {
        using var t = new TempDir();
        var (service, install, manifest) = MountedClone(t);
        var clone = t.Combine("Basis/Packages/com.x");

        using (new FileStream(t.Combine("Basis/Packages/com.x/Plugins/steam_api64.dll"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var failed = await service.SwapBackAsync(install, "com.x");
            Assert.False(failed.Ok);
            Assert.StartsWith($"Restored the manifest, but couldn't delete {clone}", failed.Error);
            Assert.True(MountService.IsWorkingClone(clone));
        }

        var result = await service.SwapBackAsync(install, "com.x");
        Assert.True(result.Ok);
        Assert.False(Directory.Exists(clone));
        Assert.Contains("https://github.com/o/r.git", File.ReadAllText(manifest));
    }

    private static (MountService Service, BasisInstall Install, string Manifest) MountedClone(TempDir t)
    {
        t.CreateDir("Basis/Assets");
        var project = t.Combine("Basis");
        t.WriteFile("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n");
        var manifest = t.WriteFile("Basis/Packages/manifest.json", """{ "dependencies": {} }""");
        t.WriteFile("Basis/Packages/com.x/package.json", """{ "name": "com.x" }""");
        t.WriteFile("Basis/Packages/com.x/Plugins/steam_api64.dll", "x");
        t.WriteFile("Basis/Packages/com.x/.git/HEAD", "ref: refs/heads/main\n");
        var registry = new MountRegistry(t.Combine("data"));
        registry.Add(new MountRecord(project, "com.x", t.Combine("Basis/Packages/com.x"), "https://github.com/o/r.git"));
        return (new MountService(new GitService(), new UnityProjectService(), registry), new BasisInstall { RepoRoot = t.Path, UnityProjectPath = project, Name = "Basis" }, manifest);
    }

    [Fact]
    public void MountResult_helpers()
    {
        Assert.True(MountResult.Success("folder").Ok);
        Assert.Equal("folder", MountResult.Success("folder").FolderPath);
        Assert.False(MountResult.Fail("boom").Ok);
        Assert.Equal("boom", MountResult.Fail("boom").Error);
    }
}
