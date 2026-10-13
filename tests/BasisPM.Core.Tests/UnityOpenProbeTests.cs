using System.Runtime.InteropServices;
using System.Text;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityOpenProbeTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "basispm-open-" + Guid.NewGuid().ToString("N"), "Basis");

    public UnityOpenProbeTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, "Temp"));
        Directory.CreateDirectory(Path.Combine(_project, "Library"));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_project)!, recursive: true);

    private string LockFile => Path.Combine(_project, "Temp", "UnityLockfile");

    [Fact]
    public void No_lockfile_means_closed() => Assert.False(UnityOpenProbe.IsOpen(_project));

    [Fact]
    public void A_lockfile_left_behind_by_a_crash_is_not_open()
    {
        File.WriteAllText(LockFile, "");
        Assert.False(UnityOpenProbe.IsOpen(_project));
    }

    [Fact]
    public void A_held_lockfile_is_open()
    {
        File.WriteAllText(LockFile, "");
        using var unity = new FileStream(LockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(UnityOpenProbe.IsOpen(_project));
    }

    [Fact]
    public void An_editor_instance_file_only_counts_for_a_live_unity_process()
    {
        File.WriteAllText(LockFile, "");
        File.WriteAllText(Path.Combine(_project, "Library", "EditorInstance.json"), $$"""{ "process_id" : {{Environment.ProcessId}}, "version" : "6000.7.0b4" }""");
        Assert.False(UnityOpenProbe.IsOpen(_project));
    }

    [Theory]
    [InlineData("""{ "process_id" : 4242, "version" : "6000.7.0b4", "app_path" : "/Applications/Unity/Hub/Editor/6000.7.0b4/Unity.app" }""", 4242)]
    [InlineData("""{ "version" : "6000.7.0b4" }""", null)]
    [InlineData("""{ "process_id" : 0 }""", null)]
    [InlineData("""{ "process_id" : "12" }""", null)]
    [InlineData("[1]", null)]
    [InlineData("not json", null)]
    public void Editor_instance_pid_is_read_safely(string json, int? expected) => Assert.Equal(expected, UnityOpenProbe.ParseEditorInstancePid(json));

    [Theory]
    [InlineData("/home/u/Basis/Basis", "/home/u/Basis/Basis/", false, true)]
    [InlineData("/home/u/Basis/basis", "/home/u/Basis/Basis", false, false)]
    [InlineData("/Users/u/Basis/basis", "/Users/u/Basis/Basis", true, true)]
    [InlineData("/home/u/Basis", "/home/u/Basis/Basis", false, false)]
    [InlineData(null, "/home/u/Basis/Basis", false, false)]
    public void Working_directories_match_the_project_folder(string? cwd, string project, bool mac, bool expected)
        => Assert.Equal(expected, UnityOpenProbe.SameFolder(cwd, project, mac ? OSPlatform.OSX : OSPlatform.Linux));

    [Fact]
    public void The_macOS_cwd_is_read_from_after_the_vnode_info()
    {
        var buffer = new byte[2 * (152 + 1024)];
        Encoding.UTF8.GetBytes("/Users/u/Basis/Basis").CopyTo(buffer, 152);
        Encoding.UTF8.GetBytes("/").CopyTo(buffer, 152 + 1024 + 152);
        Assert.Equal("/Users/u/Basis/Basis", UnityOpenProbe.CwdFromVnodePathInfo(buffer, buffer.Length));
        Assert.Null(UnityOpenProbe.CwdFromVnodePathInfo(buffer, 0));
        Assert.Null(UnityOpenProbe.CwdFromVnodePathInfo(new byte[buffer.Length], buffer.Length));
    }
}
