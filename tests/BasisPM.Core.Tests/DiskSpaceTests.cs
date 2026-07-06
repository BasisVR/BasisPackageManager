using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

/// <summary>
/// Covers the Unix mount-point resolution that replaces the old <c>Path.GetPathRoot</c> (which collapses
/// every Unix path to "/", so free space was always reported for the root filesystem). The selection is a
/// pure function, so the Linux behaviour is verifiable on any host.
/// </summary>
public sealed class DiskSpaceTests
{
    [Fact]
    public void SelectMountRoot_picks_the_longest_matching_mount()
    {
        var mounts = new[] { "/", "/home", "/home/user/data" };
        Assert.Equal("/home/user/data", DiskSpace.SelectMountRoot(mounts, "/home/user/data/Basis"));
        Assert.Equal("/home", DiskSpace.SelectMountRoot(mounts, "/home/user/Proj"));
        Assert.Equal("/", DiskSpace.SelectMountRoot(mounts, "/opt/tools"));
    }

    [Fact]
    public void SelectMountRoot_matches_whole_segments_only()
    {
        var mounts = new[] { "/", "/home" };
        Assert.Equal("/", DiskSpace.SelectMountRoot(mounts, "/home2/x"));   // "/home" must not match "/home2"
        Assert.Equal("/home", DiskSpace.SelectMountRoot(mounts, "/home"));  // exact mount counts as under it
    }

    [Fact]
    public void SelectMountRoot_tolerates_a_trailing_separator_on_the_mount()
    {
        var mounts = new[] { "/", "/mnt/data/" };
        Assert.Equal("/mnt/data/", DiskSpace.SelectMountRoot(mounts, "/mnt/data/Basis"));
    }

    [Fact]
    public void SelectMountRoot_is_null_when_nothing_contains_the_path()
    {
        Assert.Null(DiskSpace.SelectMountRoot(new[] { "/home" }, "/opt/x"));
        Assert.Null(DiskSpace.SelectMountRoot(Array.Empty<string>(), "/anything"));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1073741824, "1 GB")]
    public void Human_formats_whole_binary_units(long bytes, string expected)
        => Assert.Equal(expected, DiskSpace.Human(bytes));

    [Fact]
    public void ForPath_returns_a_real_volume_for_the_temp_dir_on_this_host()
    {
        // Integration smoke test against the actual OS: a well-known, always-mounted path must resolve
        // (Windows drive-letter path or Unix longest-prefix mount) with a positive total size.
        var info = DiskSpace.ForPath(Path.GetTempPath());
        Assert.NotNull(info);
        Assert.True(info!.TotalBytes > 0);
    }
}
