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

    [Fact]
    public void MountResult_helpers()
    {
        Assert.True(MountResult.Success("folder").Ok);
        Assert.Equal("folder", MountResult.Success("folder").FolderPath);
        Assert.False(MountResult.Fail("boom").Ok);
        Assert.Equal("boom", MountResult.Fail("boom").Error);
    }
}
