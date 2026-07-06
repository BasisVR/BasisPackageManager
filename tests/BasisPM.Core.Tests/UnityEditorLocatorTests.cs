using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

/// <summary>
/// Coverage for resolving a Unity editor from a folder the user points at (the "no Unity Hub" path).
/// The pure, OS-parameterised pieces (candidate paths, version recognition, Info.plist parsing) are
/// verified for every OS on any host; <see cref="UnityEditorLocator.TryResolve"/> is exercised against
/// a fake editor tree laid out for the running OS.
/// </summary>
public sealed class UnityEditorLocatorTests
{
    // ---- IsEditorVersion (strict installer/Hub folder form) ----

    [Theory]
    [InlineData("6000.0.30f1", true)]
    [InlineData("2022.3.10f1", true)]
    [InlineData("2023.2.0b3", true)]
    [InlineData("6000.0.1a1", true)]
    [InlineData("6000.0.1p2", true)]
    [InlineData("6000.0.30F1", true)]   // channel letter is case-insensitive
    [InlineData("1.2.3", false)]        // no channel/build — a generic semver folder, not a Unity editor
    [InlineData("6000.0.30", false)]
    [InlineData("6000.0.30f", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEditorVersion_matches_only_the_strict_installer_form(string? input, bool expected)
        => Assert.Equal(expected, UnityEditorLocator.IsEditorVersion(input));

    // ---- VersionFromPath ----

    [Theory]
    [InlineData(@"C:\Program Files\Unity\Hub\Editor\6000.0.30f1\Editor\Unity.exe", "6000.0.30f1")]
    [InlineData("/Applications/Unity/Hub/Editor/2022.3.10f1/Unity.app/Contents/MacOS/Unity", "2022.3.10f1")]
    [InlineData("/opt/unity/6000.0.30f1/Editor/Unity", "6000.0.30f1")]
    public void VersionFromPath_reads_the_version_named_segment(string path, string expected)
        => Assert.Equal(expected, UnityEditorLocator.VersionFromPath(path));

    [Theory]
    [InlineData(@"C:\Unity\Editor\Unity.exe")]      // no version anywhere in the path
    [InlineData("/home/user/1.2.3/Editor/Unity")]   // generic semver, not a Unity editor version
    public void VersionFromPath_is_null_without_a_version_segment(string path)
        => Assert.Null(UnityEditorLocator.VersionFromPath(path));

    [Fact]
    public void VersionFromPath_prefers_the_segment_nearest_the_executable()
    {
        var path = @"C:\Unity\6000.0.10f1\copy\6000.0.30f1\Editor\Unity.exe";
        Assert.Equal("6000.0.30f1", UnityEditorLocator.VersionFromPath(path));
    }

    // ---- EditorExecutableCandidates (pure, per-OS) ----

    [Fact]
    public void Candidates_on_Windows_look_under_Editor_then_the_root()
    {
        var c = UnityEditorLocator.EditorExecutableCandidates(@"C:\U\6000.0.30f1", OSPlatform.Windows).ToList();
        Assert.Equal(new[]
        {
            Path.Combine(@"C:\U\6000.0.30f1", "Editor", "Unity.exe"),
            Path.Combine(@"C:\U\6000.0.30f1", "Unity.exe"),
        }, c);
    }

    [Fact]
    public void Candidates_on_Linux_use_the_extensionless_binary()
    {
        var c = UnityEditorLocator.EditorExecutableCandidates("/opt/6000.0.30f1", OSPlatform.Linux).ToList();
        Assert.Equal(new[]
        {
            Path.Combine("/opt/6000.0.30f1", "Editor", "Unity"),
            Path.Combine("/opt/6000.0.30f1", "Unity"),
        }, c);
    }

    [Fact]
    public void Candidates_on_macOS_are_app_bundles_and_include_a_directly_picked_bundle()
    {
        var picked = "/Applications/Unity/Hub/Editor/6000.0.30f1/Unity.app";
        var c = UnityEditorLocator.EditorExecutableCandidates(picked, OSPlatform.OSX).ToList();
        Assert.Equal(picked, c[0]); // a bundle the user picked directly comes first
        Assert.Contains(Path.Combine(picked, "Unity.app"), c);
        Assert.Contains(Path.Combine(picked, "Editor", "Unity.app"), c);
    }

    [Fact]
    public void Candidates_on_macOS_from_a_version_root_do_not_include_the_root_itself()
    {
        var root = "/Applications/Unity/Hub/Editor/6000.0.30f1";
        var c = UnityEditorLocator.EditorExecutableCandidates(root, OSPlatform.OSX).ToList();
        Assert.DoesNotContain(root, c);
        Assert.Equal(Path.Combine(root, "Unity.app"), c[0]);
    }

    // ---- VersionFromInfoPlistText ----

    [Fact]
    public void VersionFromInfoPlistText_reads_CFBundleVersion()
    {
        var plist = "<plist><dict><key>CFBundleName</key><string>Unity</string>" +
                    "<key>CFBundleVersion</key><string>6000.0.30f1</string></dict></plist>";
        Assert.Equal("6000.0.30f1", UnityEditorLocator.VersionFromInfoPlistText(plist));
    }

    [Fact]
    public void VersionFromInfoPlistText_is_null_when_absent_or_not_a_unity_version()
    {
        Assert.Null(UnityEditorLocator.VersionFromInfoPlistText("<plist><dict></dict></plist>"));
        Assert.Null(UnityEditorLocator.VersionFromInfoPlistText("<key>CFBundleVersion</key><string>1.2.3</string>"));
        Assert.Null(UnityEditorLocator.VersionFromInfoPlistText(null));
    }

    // ---- VersionFromProductVersion (Windows Unity.exe ProductVersion) ----

    [Theory]
    [InlineData("6000.5.2f1_eb73d3b415a1", "6000.5.2f1")]  // real observed format: version_changeset
    [InlineData("2022.3.22f1_887be4894c44", "2022.3.22f1")]
    [InlineData("6000.5.2f1", "6000.5.2f1")]               // already clean
    [InlineData("  6000.5.2f1_abc  ", "6000.5.2f1")]       // trimmed
    public void VersionFromProductVersion_strips_the_changeset_suffix(string input, string expected)
        => Assert.Equal(expected, UnityEditorLocator.VersionFromProductVersion(input));

    [Theory]
    [InlineData("1.2.3_abc")]   // not a Unity editor version even after stripping
    [InlineData("garbage")]
    [InlineData("")]
    [InlineData(null)]
    public void VersionFromProductVersion_is_null_for_non_unity_values(string? input)
        => Assert.Null(UnityEditorLocator.VersionFromProductVersion(input));

    // ---- TryResolve (filesystem, running OS) ----

    [Fact]
    public void TryResolve_finds_the_editor_under_a_version_named_folder()
    {
        using var tmp = new TempDir("editor");
        var root = CreateFakeEditor(tmp, "6000.0.30f1");

        Assert.True(new UnityEditorLocator().TryResolve(root, out var editor));
        Assert.Equal("6000.0.30f1", editor.Version);
        Assert.True(editor.IsManual);
    }

    [Fact]
    public void TryResolve_accepts_the_Editor_subfolder_directly()
    {
        using var tmp = new TempDir("editor");
        CreateFakeEditor(tmp, "6000.0.30f1");
        var editorSub = tmp.Combine("6000.0.30f1", "Editor");

        Assert.True(new UnityEditorLocator().TryResolve(editorSub, out var editor));
        Assert.Equal("6000.0.30f1", editor.Version);
    }

    [Fact]
    public void TryResolve_returns_false_when_there_is_no_editor_executable()
    {
        using var tmp = new TempDir("editor");
        var empty = tmp.CreateDir("6000.0.30f1");
        Assert.False(new UnityEditorLocator().TryResolve(empty, out _));
    }

    [Fact]
    public void TryResolve_returns_false_for_missing_or_blank_input()
    {
        var locator = new UnityEditorLocator();
        Assert.False(locator.TryResolve(null, out _));
        Assert.False(locator.TryResolve("   ", out _));
        Assert.False(locator.TryResolve(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N")), out _));
    }

    /// <summary>Lays out a fake editor tree matching the running OS; returns the version-root folder.</summary>
    private static string CreateFakeEditor(TempDir tmp, string version)
    {
        var root = tmp.CreateDir(version);
        var editorDir = tmp.CreateDir(Path.Combine(version, "Editor"));
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Directory.CreateDirectory(Path.Combine(editorDir, "Unity.app"));
        else
            File.WriteAllText(Path.Combine(editorDir, RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Unity.exe" : "Unity"), "");
        return root;
    }
}
