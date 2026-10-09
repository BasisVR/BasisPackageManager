using System.Globalization;
using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class InvariantGlobalizationTests
{
    [Fact]
    public void Needed_on_Linux_when_no_ICU_loads() => Assert.True(InvariantGlobalization.IsNeeded(OSPlatform.Linux, null, _ => false));

    [Theory]
    [InlineData(50)]
    [InlineData(74)]
    [InlineData(255)]
    public void Not_needed_when_a_major_versioned_ICU_pair_loads(int major) =>
        Assert.False(InvariantGlobalization.IsNeeded(OSPlatform.Linux, null, lib => lib == "libicuuc.so." + major || lib == "libicui18n.so." + major));

    [Fact]
    public void Needed_when_libicuuc_loads_without_libicui18n() => Assert.True(InvariantGlobalization.IsNeeded(OSPlatform.Linux, null, lib => lib == "libicuuc.so.74"));

    [Fact]
    public void Needed_when_the_two_libraries_are_different_majors() => Assert.True(InvariantGlobalization.IsNeeded(OSPlatform.Linux, null, lib => lib is "libicuuc.so.74" or "libicui18n.so.72"));

    [Fact]
    public void Not_needed_when_CLR_ICU_VERSION_OVERRIDE_is_set() => Assert.False(InvariantGlobalization.IsNeeded(OSPlatform.Linux, "72.1", _ => false));

    [Fact]
    public void Never_needed_on_Windows_or_macOS()
    {
        Assert.False(InvariantGlobalization.IsNeeded(OSPlatform.Windows, null, _ => false));
        Assert.False(InvariantGlobalization.IsNeeded(OSPlatform.OSX, null, _ => false));
    }

    [Fact]
    public void Finds_the_ICU_the_runtime_loaded_on_Linux()
    {
        if (!OperatingSystem.IsLinux()) return;
        _ = CultureInfo.CurrentCulture.CompareInfo;
        if (File.ReadAllText("/proc/self/maps").Contains("libicuuc", StringComparison.Ordinal)) Assert.False(InvariantGlobalization.IsNeeded(OSPlatform.Linux, null, InvariantGlobalization.CanLoad));
    }
}
