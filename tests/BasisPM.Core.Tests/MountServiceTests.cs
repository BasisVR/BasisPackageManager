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
    public void MountResult_helpers()
    {
        Assert.True(MountResult.Success("folder").Ok);
        Assert.Equal("folder", MountResult.Success("folder").FolderPath);
        Assert.False(MountResult.Fail("boom").Ok);
        Assert.Equal("boom", MountResult.Fail("boom").Error);
    }
}
