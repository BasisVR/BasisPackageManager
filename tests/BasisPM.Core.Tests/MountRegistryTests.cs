using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class MountRegistryTests
{
    private static MountRecord Record(string install, string pkg, string folder = "folder", string original = "https://github.com/x/y.git")
        => new(install, pkg, folder, original);

    [Fact]
    public void Add_then_Find_returns_the_record()
    {
        using var t = new TempDir();
        var reg = new MountRegistry(t.Path);
        reg.Add(Record(@"C:\Install", "com.x"));

        var found = reg.Find(@"C:\Install", "com.x");
        Assert.NotNull(found);
        Assert.Equal("com.x", found!.PackageId);
    }

    [Fact]
    public void Find_is_case_insensitive()
    {
        using var t = new TempDir();
        var reg = new MountRegistry(t.Path);
        reg.Add(Record(@"C:\Install", "com.x"));

        Assert.NotNull(reg.Find(@"c:\install", "COM.X"));
    }

    [Fact]
    public void Add_replaces_an_existing_mount_for_the_same_install_and_package()
    {
        using var t = new TempDir();
        var reg = new MountRegistry(t.Path);
        reg.Add(Record(@"C:\Install", "com.x", folder: "old"));
        reg.Add(Record(@"C:\Install", "com.x", folder: "new"));

        Assert.Equal("new", reg.Find(@"C:\Install", "com.x")!.FolderPath);
        Assert.Single(reg.ForInstall(@"C:\Install"));
    }

    [Fact]
    public void ForInstall_returns_all_mounts_for_that_install()
    {
        using var t = new TempDir();
        var reg = new MountRegistry(t.Path);
        reg.Add(Record(@"C:\Install", "com.x"));
        reg.Add(Record(@"C:\Install", "com.y"));
        reg.Add(Record(@"C:\Other", "com.z"));

        Assert.Equal(2, reg.ForInstall(@"C:\Install").Count);
    }

    [Fact]
    public void Remove_deletes_the_record()
    {
        using var t = new TempDir();
        var reg = new MountRegistry(t.Path);
        reg.Add(Record(@"C:\Install", "com.x"));
        reg.Remove(@"C:\Install", "com.x");

        Assert.Null(reg.Find(@"C:\Install", "com.x"));
    }

    [Fact]
    public void Records_persist_across_instances()
    {
        using var t = new TempDir();
        new MountRegistry(t.Path).Add(Record(@"C:\Install", "com.x", original: "https://github.com/a/b.git"));

        var reloaded = new MountRegistry(t.Path);
        Assert.Equal("https://github.com/a/b.git", reloaded.Find(@"C:\Install", "com.x")!.OriginalManifestValue);
    }

    [Fact]
    public void Find_returns_null_when_absent()
    {
        using var t = new TempDir();
        Assert.Null(new MountRegistry(t.Path).Find(@"C:\Nope", "com.x"));
    }
}
