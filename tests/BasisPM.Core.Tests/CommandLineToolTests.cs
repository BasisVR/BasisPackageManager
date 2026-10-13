using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class CommandLineToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "basispm-cli-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("basispm", "/a/B.AppImage", true)]
    [InlineData("/home/u/.local/bin/basispm", "/a/B.AppImage", true)]
    [InlineData("./BasisPackageManager.AppImage", "/a/B.AppImage", false)]
    [InlineData("basispm", null, false)]
    [InlineData(null, "/a/B.AppImage", false)]
    public void Only_an_appimage_started_as_basispm_runs_the_console(string? argv0, string? appImage, bool expected)
        => Assert.Equal(expected, CommandLineTool.IsCliInvocation(argv0, appImage));

    [Fact]
    public void The_link_points_at_the_appimage_on_linux_and_into_the_bundle_on_macOS()
    {
        Assert.Equal("/home/u/Apps/Basis.AppImage", CommandLineTool.Target(OSPlatform.Linux, "/home/u/Apps/Basis.AppImage", "/tmp/.mount_x/usr/bin/", _ => true));
        Assert.Null(CommandLineTool.Target(OSPlatform.Linux, null, "/opt/dev/bin/", _ => true));
        var bundle = "/Applications/Basis Package Manager.app/Contents/MacOS/";
        Assert.Equal(Path.Combine(bundle, "basispm"), CommandLineTool.Target(OSPlatform.OSX, null, bundle, _ => true));
        Assert.Null(CommandLineTool.Target(OSPlatform.OSX, null, "/Users/u/src/bin/Debug/net9.0/", _ => true));
        Assert.Null(CommandLineTool.Target(OSPlatform.OSX, null, bundle, _ => false));
        Assert.Null(CommandLineTool.Target(OSPlatform.Windows, null, @"C:\Users\u\AppData\Local\BasisPackageManager\current\", _ => true));
    }

    [Fact]
    public void macOS_tries_usr_local_bin_before_the_users_own_folder()
    {
        Assert.Equal(new[] { "/usr/local/bin", "/Users/u/.local/bin" }, CommandLineTool.LinkFolders(OSPlatform.OSX, "/Users/u"));
        Assert.Equal(new[] { "/home/u/.local/bin" }, CommandLineTool.LinkFolders(OSPlatform.Linux, "/home/u/"));
    }

    [Theory]
    [InlineData("/usr/bin:/home/u/.local/bin/", "/home/u/.local/bin", true)]
    [InlineData("/usr/bin:/home/u/.local/bin2", "/home/u/.local/bin", false)]
    [InlineData(null, "/home/u/.local/bin", false)]
    public void On_path_matches_whole_entries(string? path, string folder, bool expected) => Assert.Equal(expected, CommandLineTool.IsOnPath(path, folder));

    [UnixFact]
    public void Installing_links_the_tool_and_replaces_an_older_link()
    {
        var target = Path.Combine(_root, "Basis.AppImage");
        var older = Path.Combine(_root, "Old.AppImage");
        Directory.CreateDirectory(_root);
        File.WriteAllText(target, "");
        File.WriteAllText(older, "");
        var folder = Path.Combine(_root, "bin");
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(Path.Combine(folder, "basispm"), older);

        var (link, onPath) = CommandLineTool.InstallLink(target, new[] { folder }, "/usr/bin:" + folder);

        Assert.Equal(Path.Combine(folder, "basispm"), link);
        Assert.True(onPath);
        Assert.Equal(target, new FileInfo(link).LinkTarget);
    }

    [UnixFact]
    public void Installing_never_overwrites_a_real_file()
    {
        var folder = Path.Combine(_root, "bin");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "basispm"), "mine");
        Assert.Throws<IOException>(() => CommandLineTool.InstallLink(Path.Combine(_root, "x"), new[] { folder }, null));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(folder, "basispm")));
    }
}
