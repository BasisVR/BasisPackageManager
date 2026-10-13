using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PlatformSupportTests
{
    [Fact]
    public void Linux_hub_commands_have_no_leading_double_dash()
    {
        Assert.Equal(new[] { "--headless", "editors", "--installed" }, UnityHubService.HubArguments(OSPlatform.Linux, "editors", "--installed"));
        Assert.Equal(new[] { "--", "--headless", "editors", "--installed" }, UnityHubService.HubArguments(OSPlatform.OSX, "editors", "--installed"));
        Assert.Equal(new[] { "--", "--headless", "install" }, UnityHubService.HubArguments(OSPlatform.Windows, "install"));
    }

    [Theory]
    [InlineData("LINUX", null, null, false)]
    [InlineData("LINUX", ":0", null, true)]
    [InlineData("LINUX", null, "wayland-0", true)]
    [InlineData("OSX", null, null, true)]
    [InlineData("WINDOWS", null, null, true)]
    public void The_hub_only_runs_on_linux_with_a_display(string os, string? display, string? wayland, bool expected)
        => Assert.Equal(expected, UnityHubService.CanRunHeadless(OSPlatform.Create(os), display, wayland));

    [Theory]
    [InlineData("Apple silicon", "arm64")]
    [InlineData("Intel", "x86_64")]
    [InlineData("Windows Arm64", "arm64")]
    [InlineData("Windows x64", "x86_64")]
    [InlineData("", null)]
    public void Hub_architecture_labels_map_to_architectures(string label, string? expected) => Assert.Equal(expected, UnityHubService.HubLabelArchitecture(label));

    [Fact]
    public void Hub_info_names_the_hub_executable()
    {
        Assert.Equal(@"C:\Program Files\Unity Hub\Unity Hub.exe", UnityHubService.ParseHubInfoExecutable("""{"version":"3.22.2","executablePath":"C:\\Program Files\\Unity Hub\\Unity Hub.exe"}"""));
        Assert.Null(UnityHubService.ParseHubInfoExecutable("""{"version":"3.22.2"}"""));
        Assert.Null(UnityHubService.ParseHubInfoExecutable("not json"));
    }

    [Fact]
    public void Only_the_macOS_git_placeholder_needs_the_developer_tools()
    {
        Assert.True(GitService.IsAppleGitStub(OSPlatform.OSX, "/usr/bin/git"));
        Assert.False(GitService.IsAppleGitStub(OSPlatform.OSX, "/opt/homebrew/bin/git"));
        Assert.False(GitService.IsAppleGitStub(OSPlatform.Linux, "/usr/bin/git"));
        Assert.True(GitService.IsDeveloperDirectoryWithGit(0, "/Library/Developer/CommandLineTools\n", p => p == "/Library/Developer/CommandLineTools/usr/bin/git"));
        Assert.False(GitService.IsDeveloperDirectoryWithGit(2, "xcode-select: error: unable to get active developer directory", _ => true));
        Assert.False(GitService.IsDeveloperDirectoryWithGit(0, "/Applications/Xcode.app/Contents/Developer", _ => false));
    }

    [Fact]
    public void Git_install_pages_follow_the_platform()
    {
        Assert.Equal("https://git-scm.com/install/windows", GitService.InstallPage(OSPlatform.Windows));
        Assert.Equal("https://git-scm.com/install/mac", GitService.InstallPage(OSPlatform.OSX));
        Assert.Equal("https://git-scm.com/install/linux", GitService.InstallPage(OSPlatform.Linux));
    }

    [Fact]
    public void Steam_is_looked_for_in_the_packaged_installs_too()
    {
        var linux = BasisServerService.SteamCandidates(OSPlatform.Linux, "/home/u", "", "");
        Assert.Contains("/usr/games/steam", linux);
        Assert.Contains("/var/lib/flatpak/exports/bin/com.valvesoftware.Steam", linux);
        Assert.Contains("/home/u/.local/share/flatpak/exports/bin/com.valvesoftware.Steam", linux);
        Assert.Contains("/Applications/Steam.app/Contents/MacOS/steam_osx", BasisServerService.SteamCandidates(OSPlatform.OSX, "/Users/u", "", ""));
        Assert.Equal(Path.Combine(@"C:\Program Files (x86)", "Steam", "steam.exe"), BasisServerService.SteamCandidates(OSPlatform.Windows, "", @"C:\Program Files (x86)", @"C:\Program Files")[0]);
    }
}
