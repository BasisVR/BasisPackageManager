using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

/// <summary>
/// Cross-platform helper coverage. These exercise the <b>pure, OS-parameterised</b> decisions
/// (<see cref="Platform"/>, <see cref="AppLauncher"/>, <see cref="ExecutableFinder"/>) so every
/// Windows/macOS/Linux branch is verified on any host — not just the one the tests run on.
/// </summary>
public sealed class PlatformTests
{
    [Fact]
    public void PathComparison_is_case_sensitive_only_on_Linux()
    {
        Assert.Equal(StringComparison.Ordinal, Platform.PathComparison(OSPlatform.Linux));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, Platform.PathComparison(OSPlatform.Windows));
        Assert.Equal(StringComparison.OrdinalIgnoreCase, Platform.PathComparison(OSPlatform.OSX));
    }

    // ---- AppLauncher.AppBundlePath ----

    [Theory]
    [InlineData("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub", "/Applications/Unity Hub.app")]
    [InlineData("/Applications/Unity Hub.app", "/Applications/Unity Hub.app")]
    [InlineData("/Applications/Unity/Hub/Editor/6000.0.0f1/Unity.app/Contents/MacOS/Unity", "/Applications/Unity/Hub/Editor/6000.0.0f1/Unity.app")]
    public void AppBundlePath_finds_enclosing_bundle(string input, string expected)
        => Assert.Equal(expected, AppLauncher.AppBundlePath(input));

    [Theory]
    [InlineData("/usr/bin/unityhub")]
    [InlineData(@"C:\Program Files\Unity Hub\Unity Hub.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void AppBundlePath_is_null_when_there_is_no_bundle(string? input)
        => Assert.Null(AppLauncher.AppBundlePath(input));

    // ---- AppLauncher.GuiAppSpec ----

    [Fact]
    public void GuiAppSpec_runs_the_executable_directly_on_Windows_and_Linux()
    {
        var win = AppLauncher.GuiAppSpec(OSPlatform.Windows, @"C:\Program Files\Unity Hub\Unity Hub.exe");
        Assert.Equal(@"C:\Program Files\Unity Hub\Unity Hub.exe", win.FileName);
        Assert.False(win.UseShellExecute);
        Assert.Empty(win.Arguments);

        var lin = AppLauncher.GuiAppSpec(OSPlatform.Linux, "/opt/unityhub/unityhub", new[] { "-projectPath", "/home/u/Proj" });
        Assert.Equal("/opt/unityhub/unityhub", lin.FileName);
        Assert.Equal(new[] { "-projectPath", "/home/u/Proj" }, lin.Arguments);
        Assert.False(lin.UseShellExecute);
    }

    [Fact]
    public void GuiAppSpec_opens_the_bundle_via_open_on_macOS_not_the_inner_binary()
    {
        // This is the actual bug the launcher fixes: FindHubPath returns the inner Mach-O binary,
        // which UseShellExecute=true would hand to `open` and silently fail.
        var spec = AppLauncher.GuiAppSpec(OSPlatform.OSX, "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub");
        Assert.Equal("open", spec.FileName);
        Assert.Equal(new[] { "/Applications/Unity Hub.app" }, spec.Arguments);
        Assert.False(spec.UseShellExecute);
    }

    [Fact]
    public void GuiAppSpec_passes_args_after_open_dash_dash_args_on_macOS()
    {
        var spec = AppLauncher.GuiAppSpec(OSPlatform.OSX,
            "/Applications/Unity/Hub/Editor/6000.0.0f1/Unity.app",
            new[] { "-projectPath", "/Users/u/Proj" });
        Assert.Equal("open", spec.FileName);
        Assert.Equal(
            new[] { "/Applications/Unity/Hub/Editor/6000.0.0f1/Unity.app", "--args", "-projectPath", "/Users/u/Proj" },
            spec.Arguments);
    }

    [Fact]
    public void GuiAppSpec_execs_directly_on_macOS_when_the_path_is_not_a_bundle()
    {
        var spec = AppLauncher.GuiAppSpec(OSPlatform.OSX, "/opt/unityhub/unityhub");
        Assert.Equal("/opt/unityhub/unityhub", spec.FileName);
        Assert.False(spec.UseShellExecute);
    }

    // ---- ExecutableFinder ----

    [Fact]
    public void Extensions_add_exe_only_on_Windows()
    {
        Assert.Contains(".exe", ExecutableFinder.Extensions(OSPlatform.Windows));
        Assert.Equal(new[] { "" }, ExecutableFinder.Extensions(OSPlatform.Linux));
        Assert.Equal(new[] { "" }, ExecutableFinder.Extensions(OSPlatform.OSX));
    }

    [Fact]
    public void SelectFromPath_returns_the_first_matching_directory_in_order()
    {
        var pathEnv = string.Join(Path.PathSeparator, "/a/bin", "/b/bin", "/c/bin");
        var b = Path.Combine("/b/bin", "git");
        var c = Path.Combine("/c/bin", "git");
        var present = new HashSet<string> { b, c };

        var found = ExecutableFinder.SelectFromPath("git", pathEnv, new[] { "" }, present.Contains);

        Assert.Equal(b, found); // /b before /c
    }

    [Fact]
    public void SelectFromPath_tries_the_extensions_in_order()
    {
        var pathEnv = "tools";
        var exe = Path.Combine("tools", "git.exe");
        var found = ExecutableFinder.SelectFromPath(
            "git", pathEnv, ExecutableFinder.Extensions(OSPlatform.Windows), p => p == exe);
        Assert.Equal(exe, found);
    }

    [Fact]
    public void SelectFromPath_is_null_when_nothing_matches_or_path_is_empty()
    {
        Assert.Null(ExecutableFinder.SelectFromPath("git", null, new[] { "" }, _ => true));
        Assert.Null(ExecutableFinder.SelectFromPath("git", "", new[] { "" }, _ => true));
        Assert.Null(ExecutableFinder.SelectFromPath("git", string.Join(Path.PathSeparator, "/a", "/b"), new[] { "" }, _ => false));
    }
}
