using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityHubDiscoveryTests
{
    [Fact]
    public void Windows_uses_the_program_files_folders_of_this_machine()
    {
        var candidates = UnityHubService.HubCandidates(OSPlatform.Windows, @"C:\Users\u", @"D:\Program Files", @"D:\Program Files (x86)");
        Assert.Equal(new[] { Path.Combine(@"D:\Program Files", "Unity Hub", "Unity Hub.exe"), Path.Combine(@"D:\Program Files (x86)", "Unity Hub", "Unity Hub.exe") }, candidates);
    }

    [Fact]
    public void macOS_checks_the_users_own_applications_folder_too()
        => Assert.Equal(new[] { "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub", "/Users/u/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" },
            UnityHubService.HubCandidates(OSPlatform.OSX, "/Users/u/", "", ""));

    [Fact]
    public void Linux_includes_the_deb_appimage_and_flatpak_installs()
    {
        var candidates = UnityHubService.HubCandidates(OSPlatform.Linux, "/home/u", "", "");
        Assert.Contains("/usr/lib/unityhub/unityhub", candidates);
        Assert.Contains("/opt/unityhub/unityhub", candidates);
        Assert.Contains("/home/u/Applications/Unity Hub.AppImage", candidates);
        Assert.Contains("/var/lib/flatpak/exports/bin/com.unity.UnityHub", candidates);
        Assert.Contains("/home/u/.local/share/flatpak/exports/bin/com.unity.UnityHub", candidates);
    }

    [Fact]
    public void The_server_runtime_folder_is_only_a_real_dotnet_install()
    {
        Assert.Equal("/home/u/.dotnet", BasisServerService.DotNetRoot("/home/u/.dotnet/dotnet", d => d.Replace('\\', '/') == "/home/u/.dotnet/host/fxr"));
        Assert.Null(BasisServerService.DotNetRoot("/usr/bin/snap", _ => false));
        Assert.Null(BasisServerService.DotNetRoot(null, _ => true));
    }

    [Fact]
    public void An_override_can_name_the_hub_app_bundle()
    {
        var bundle = Path.Combine(Path.GetTempPath(), "basispm-hub-" + Guid.NewGuid().ToString("N"), "Unity Hub.app");
        var inner = Path.Combine(bundle, "Contents", "MacOS", "Unity Hub");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(inner)!);
            File.WriteAllText(inner, "");
            Assert.Equal(inner, UnityHubService.ResolveOverride(bundle));
            Assert.Equal(inner, UnityHubService.ResolveOverride(" " + inner + " "));
            Assert.Null(UnityHubService.ResolveOverride(Path.GetDirectoryName(bundle)));
            Assert.Null(UnityHubService.ResolveOverride("  "));
        }
        finally { Directory.Delete(Path.GetDirectoryName(bundle)!, recursive: true); }
    }
}
