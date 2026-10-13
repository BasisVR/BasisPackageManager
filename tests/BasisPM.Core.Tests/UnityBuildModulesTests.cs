using System.Runtime.InteropServices;
using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityBuildModulesTests
{
    private const string Beta = """
        {
          "version": "6000.7.0b4", "stream": "BETA", "shortRevision": "abc123",
          "downloads": [
            { "platform": "LINUX", "architecture": "X86_64", "modules": [ { "id": "android" }, { "id": "ios" }, { "id": "linux-il2cpp" }, { "id": "linux-coreclr" }, { "id": "mac-coreclr" }, { "id": "windows-coreclr" }, { "id": "mac-mono" }, { "id": "windows-mono" }, { "id": "documentation" } ] },
            { "platform": "MAC_OS", "architecture": "ARM64", "modules": [ { "id": "android" }, { "id": "linux-il2cpp" }, { "id": "linux-mono" }, { "id": "windows-coreclr" }, { "id": "mac-coreclr" }, { "id": "linux-coreclr" }, { "id": "mac-il2cpp" }, { "id": "windows-mono" }, { "id": "documentation" } ] },
            { "platform": "MAC_OS", "architecture": "X86_64", "modules": [ { "id": "android" }, { "id": "mac-il2cpp" } ] },
            { "platform": "WINDOWS", "architecture": "X86_64", "modules": [ { "id": "android" }, { "id": "linux-il2cpp" }, { "id": "linux-mono" }, { "id": "windows-coreclr" }, { "id": "mac-coreclr" }, { "id": "linux-coreclr" }, { "id": "mac-mono" }, { "id": "windows-il2cpp" }, { "id": "documentation" } ] }
          ]
        }
        """;

    private static UnityRelease Release(string json = Beta) => JsonSerializer.Deserialize<UnityRelease>(json)!;

    [Fact]
    public void Coreclr_comes_first_and_only_modules_this_os_offers_are_listed()
    {
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr", "mac-coreclr", "android", "windows-il2cpp", "linux-il2cpp", "linux-mono", "mac-mono", "documentation" },
            UnityBuildModules.OfferedFor(Release(), OSPlatform.Windows, Architecture.X64));
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr", "mac-coreclr", "android", "windows-mono", "linux-il2cpp", "mac-mono", "documentation" },
            UnityBuildModules.OfferedFor(Release(), OSPlatform.Linux, Architecture.X64));
    }

    [Fact]
    public void Apple_silicon_uses_its_own_download_and_intel_its_own()
    {
        Assert.Contains("mac-coreclr", UnityBuildModules.OfferedFor(Release(), OSPlatform.OSX, Architecture.Arm64));
        Assert.Equal(new[] { "android", "mac-il2cpp" }, UnityBuildModules.OfferedFor(Release(), OSPlatform.OSX, Architecture.X64));
    }

    [Fact]
    public void A_missing_architecture_falls_back_to_the_platform()
        => Assert.Contains("linux-coreclr", UnityBuildModules.OfferedFor(Release(), OSPlatform.Linux, Architecture.Arm64));

    [Fact]
    public void Releases_without_download_data_use_the_host_list()
    {
        var release = Release("""{ "version": "6000.3.26f1", "downloads": null }""");
        Assert.Equal(new[] { "android", "windows-mono", "linux-il2cpp", "linux-mono", "mac-il2cpp", "documentation" }, UnityBuildModules.OfferedFor(release, OSPlatform.OSX, Architecture.Arm64));
    }

    [Theory]
    [InlineData("6000.6.5f1", false)]
    [InlineData("6000.7.0a1", true)]
    [InlineData("6000.7.0b4", true)]
    [InlineData("6001.0.0f1", true)]
    [InlineData("2022.3.10f1", false)]
    [InlineData(null, false)]
    public void Coreclr_starts_at_6000_7(string? version, bool expected) => Assert.Equal(expected, UnityBuildModules.HasCoreClr(version));

    [Fact]
    public void Each_host_falls_back_to_modules_it_can_install()
    {
        Assert.Equal(new[] { "android", "windows-il2cpp", "linux-il2cpp", "linux-mono", "mac-mono", "documentation" }, UnityBuildModules.Fallback(OSPlatform.Windows, "6000.3.1f1"));
        Assert.Equal(new[] { "android", "windows-mono", "linux-il2cpp", "mac-mono", "documentation" }, UnityBuildModules.Fallback(OSPlatform.Linux, null));
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr", "mac-coreclr", "android", "windows-mono", "linux-il2cpp", "linux-mono", "mac-il2cpp", "documentation" }, UnityBuildModules.Fallback(OSPlatform.OSX, "6000.7.1f1"));
    }

    [Fact]
    public void Coreclr_for_windows_linux_and_the_host_is_ticked_by_default()
    {
        var offered = UnityBuildModules.Fallback(OSPlatform.OSX, "6000.7.0b4");
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr", "mac-coreclr" }, UnityBuildModules.Preselected(OSPlatform.OSX, offered));
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr" }, UnityBuildModules.Preselected(OSPlatform.Windows, UnityBuildModules.Fallback(OSPlatform.Windows, "6000.7.0b4")));
        Assert.Empty(UnityBuildModules.Preselected(OSPlatform.Linux, UnityBuildModules.Fallback(OSPlatform.Linux, "6000.6.0f1")));
    }
}
