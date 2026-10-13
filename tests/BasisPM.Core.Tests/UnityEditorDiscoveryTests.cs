using System.Runtime.InteropServices;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityEditorDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "basispm-editors-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("6000.7.0b4", "6000.7.0b4")]
    [InlineData("2022.3.10f1-x86_64", "2022.3.10f1")]
    [InlineData("2022.3.10f1-arm64", "2022.3.10f1")]
    [InlineData("Unity-6000.3.24f1", "6000.3.24f1")]
    [InlineData("6000.0.30f1c1", "6000.0.30f1c1")]
    [InlineData("1.2.3", null)]
    [InlineData("Editor", null)]
    [InlineData(null, null)]
    public void Folder_names_give_the_version_like_unity_hub_reads_them(string? folder, string? expected) => Assert.Equal(expected, UnityEditorLocator.FolderVersion(folder));

    [Theory]
    [InlineData("2022.3.10f1-arm64", "arm64")]
    [InlineData("2022.3.10f1-x86_64", "x86_64")]
    [InlineData("2022.3.10f1", null)]
    public void A_second_architecture_is_named_by_its_folder_suffix(string folder, string? expected) => Assert.Equal(expected, UnityEditorLocator.FolderArchitecture(folder));

    [Fact]
    public void A_suffixed_folder_in_a_path_still_names_the_version()
        => Assert.Equal("2022.3.10f1", UnityEditorLocator.VersionFromPath("/Applications/Unity/Hub/Editor/2022.3.10f1-x86_64/Unity.app"));

    [Fact]
    public void Executable_headers_give_the_architecture()
    {
        Assert.Equal("arm64", UnityEditorLocator.ArchitectureFromHeader(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x0C, 0x00, 0x00, 0x01 }));
        Assert.Equal("x86_64", UnityEditorLocator.ArchitectureFromHeader(new byte[] { 0xCF, 0xFA, 0xED, 0xFE, 0x07, 0x00, 0x00, 0x01 }));
        var elf = new byte[20];
        elf[0] = 0x7F; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F'; elf[18] = 0xB7;
        Assert.Equal("arm64", UnityEditorLocator.ArchitectureFromHeader(elf));
        elf[18] = 0x3E;
        Assert.Equal("x86_64", UnityEditorLocator.ArchitectureFromHeader(elf));
        var pe = new byte[0x100];
        pe[0] = (byte)'M'; pe[1] = (byte)'Z'; pe[0x3C] = 0x80; pe[0x80] = (byte)'P'; pe[0x81] = (byte)'E'; pe[0x84] = 0x64; pe[0x85] = 0xAA;
        Assert.Equal("arm64", UnityEditorLocator.ArchitectureFromHeader(pe));
        pe[0x84] = 0x64; pe[0x85] = 0x86;
        Assert.Equal("x86_64", UnityEditorLocator.ArchitectureFromHeader(pe));
        Assert.Null(UnityEditorLocator.ArchitectureFromHeader(new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 2 }));
        Assert.Null(UnityEditorLocator.ArchitectureFromHeader(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void Hub_config_folders_follow_each_platform()
    {
        Assert.Equal(new[] { "/Users/u/Library/Application Support/UnityHub" }, UnityEditorLocator.HubConfigFolders(OSPlatform.OSX, "/Users/u", "", null, ""));
        Assert.Equal(new[] { "/home/u/.config/unityhub", "/home/u/.config/UnityHub", "/home/u/.var/app/com.unity.UnityHub/config/unityhub" },
            UnityEditorLocator.HubConfigFolders(OSPlatform.Linux, "/home/u", "", null, ""));
        Assert.Equal("/x/cfg/unityhub", UnityEditorLocator.HubConfigFolders(OSPlatform.Linux, "/home/u", "", "/x/cfg/", "")[0]);
        Assert.Equal(Path.Combine(@"C:\Users\u\AppData\Roaming", "UnityHub"), UnityEditorLocator.HubConfigFolders(OSPlatform.Windows, @"C:\Users\u", @"C:\Users\u\AppData\Roaming", null, "")[0]);
    }

    [Fact]
    public void Default_editor_folders_follow_each_platform()
    {
        Assert.Equal(new[] { "/Applications/Unity/Hub/Editor" }, UnityEditorLocator.DefaultEditorRoots(OSPlatform.OSX, "/Users/u", ""));
        Assert.Equal(new[] { "/home/u/Unity/Hub/Editor" }, UnityEditorLocator.DefaultEditorRoots(OSPlatform.Linux, "/home/u/", ""));
        Assert.Equal(new[] { Path.Combine(@"C:\Program Files", "Unity", "Hub", "Editor") }, UnityEditorLocator.DefaultEditorRoots(OSPlatform.Windows, "", @"C:\Program Files"));
    }

    [Theory]
    [InlineData("\"D:\\\\Unity\\\\Editors\"", "D:\\Unity\\Editors")]
    [InlineData("\"\"", null)]
    [InlineData("{ \"a\": 1 }", null)]
    [InlineData("nope", null)]
    public void The_custom_install_folder_is_a_bare_json_string(string json, string? expected) => Assert.Equal(expected, UnityEditorLocator.ParseSecondaryInstallPath(json));

    [Fact]
    public void Hidden_and_located_editor_files_are_read_safely()
    {
        Assert.Equal(new[] { "/a/6000.0.1f1", "/b" }, UnityEditorLocator.ParseExcludedFolders("""{ "schema_version": "1", "data": [ "/a/6000.0.1f1", "/b", 3 ] }"""));
        Assert.Empty(UnityEditorLocator.ParseExcludedFolders("broken"));
        var located = UnityEditorLocator.ParseLocatedEditors("""
            { "schema_version": "2", "data": [
              { "version": "2022.3.10f1", "location": [ "/x/2022.3.10f1/Unity.app" ], "manual": true, "architecture": "arm64" },
              { "version": "6000.0.30f1", "location": [ "/y/Editor/Unity" ], "manual": true },
              { "version": "not a version", "location": [ "/z" ] },
              { "version": "6000.0.31f1", "location": [] }
            ] }
            """);
        Assert.Equal(new[] { ("2022.3.10f1", "/x/2022.3.10f1/Unity.app", "arm64"), ("6000.0.30f1", "/y/Editor/Unity", "x86_64") }, located.Select(e => (e.Version, e.Path, e.Architecture!)));
    }

    [Fact]
    public void Located_editors_may_give_a_single_location_string()
        => Assert.Equal("/x/Unity.app", Assert.Single(UnityEditorLocator.ParseLocatedEditors("""{ "data": [ { "version": "6000.0.30f1c1", "location": "/x/Unity.app" } ] }""")).Path);

    [Fact]
    public void The_machine_wide_install_folder_is_read_from_services_config()
    {
        Assert.Equal("/opt/unity-editors", UnityEditorLocator.ParseMachineWideInstallLocation("""{ "machineWideSecondaryInstallLocation": "/opt/unity-editors" }"""));
        Assert.Null(UnityEditorLocator.ParseMachineWideInstallLocation("""{ "machineWideSecondaryInstallLocation": "" }"""));
        Assert.Null(UnityEditorLocator.ParseMachineWideInstallLocation("broken"));
        Assert.Equal("/usr/share/unity3d/config", UnityEditorLocator.MachineConfigFolder(OSPlatform.Linux, null, ""));
        Assert.Equal("/Library/Application Support/Unity/config", UnityEditorLocator.MachineConfigFolder(OSPlatform.OSX, " ", ""));
        Assert.Equal("/etc/unity", UnityEditorLocator.MachineConfigFolder(OSPlatform.Linux, "/etc/unity", ""));
        Assert.Equal(Path.Combine(@"C:\ProgramData", "Unity", "config"), UnityEditorLocator.MachineConfigFolder(OSPlatform.Windows, null, @"C:\ProgramData"));
    }

    [Fact]
    public void Editors_are_found_in_hub_folders_custom_folders_and_located_entries()
    {
        var os = Platform.Current;
        var defaults = Path.Combine(_root, "Hub", "Editor");
        var custom = Path.Combine(_root, "Custom");
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(config);
        var older = MakeEditor(defaults, "6000.3.24f1");
        var newer = MakeEditor(defaults, "6000.7.0b4");
        var hidden = MakeEditor(defaults, "6000.6.5f1");
        Directory.CreateDirectory(Path.Combine(defaults, "not-an-editor"));
        Directory.CreateDirectory(Path.Combine(defaults, "6000.5.1f1"));
        var inCustom = MakeEditor(custom, "6000.7.0b4-x86_64");
        var located = MakeEditor(Path.Combine(_root, "Elsewhere"), "anything");
        File.WriteAllText(Path.Combine(config, "secondaryInstallPath.json"), System.Text.Json.JsonSerializer.Serialize(custom));
        File.WriteAllText(Path.Combine(config, "excludedEditorFolders.json"), System.Text.Json.JsonSerializer.Serialize(new { schema_version = "1", data = new[] { Path.Combine(defaults, "6000.6.5f1") } }));
        File.WriteAllText(Path.Combine(config, "editors-v2.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = "2",
            data = new object[] { new { version = "2022.3.62f1", location = new[] { located }, manual = true }, new { version = "2021.3.1f1", location = new[] { Path.Combine(_root, "gone") }, manual = true } },
        }));

        var all = UnityEditorLocator.FindInstalledIn(new[] { config, Path.Combine(_root, "missing") }, new[] { defaults }, null, os);

        Assert.Equal(new[] { "6000.7.0b4", "6000.7.0b4", "6000.3.24f1", "2022.3.62f1" }, all.Select(e => e.Version));
        Assert.Contains(all, e => e.Path == newer);
        Assert.Contains(all, e => e.Path == inCustom);
        Assert.Contains(all, e => e.Path == older);
        Assert.DoesNotContain(all, e => e.Path == hidden);
        Assert.Equal("x86_64", all.Single(e => e.Path == located).Architecture);

        var one = UnityEditorLocator.FindInstalledIn(new[] { config }, new[] { defaults }, "6000.3.24f1", os);
        Assert.Equal(older, Assert.Single(one).Path);
    }

    private static string MakeEditor(string root, string folder)
    {
        var dir = Path.Combine(root, folder);
        if (Platform.Current == OSPlatform.OSX)
        {
            var app = Path.Combine(dir, "Unity.app");
            Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
            File.WriteAllText(Path.Combine(app, "Contents", "MacOS", "Unity"), "");
            return app;
        }
        var exe = Path.Combine(dir, "Editor", Platform.Current == OSPlatform.Windows ? "Unity.exe" : "Unity");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "");
        return exe;
    }
}
