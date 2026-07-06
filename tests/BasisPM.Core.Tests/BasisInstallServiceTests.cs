using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisInstallServiceTests
{
    private readonly BasisInstallService _svc = new(new UnityProjectService(), new GitService());

    private static string MakeProject(TempDir t, string name, string version = "6000.0.25f1")
    {
        var root = t.CreateDir(name);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), $"m_EditorVersion: {version}\n");
        return root;
    }

    [Fact]
    public async Task LoadAsync_reads_a_unity_install()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Basis");
        t.WriteFile("Basis/Packages/manifest.json", """{ "dependencies": { "com.x": "1.0.0" } }""");

        var install = await _svc.LoadAsync(root);

        Assert.Equal("Basis", install.Name);
        Assert.True(install.HasUnityProject);
        Assert.Equal("6000.0.25f1", install.UnityVersion);
        Assert.Equal(root, install.UnityProjectPath);
        Assert.False(install.IsGitRepo);
        Assert.Equal("1.0.0", install.Manifest.Dependencies["com.x"]);
    }

    [Fact]
    public async Task LoadAsync_handles_a_non_unity_folder()
    {
        using var t = new TempDir();
        var plain = t.CreateDir("plain");

        var install = await _svc.LoadAsync(plain);

        Assert.False(install.HasUnityProject);
        Assert.Equal("unknown", install.UnityVersion);
        Assert.Equal(plain, install.UnityProjectPath);
    }

    [Fact]
    public async Task LoadAsync_trims_the_alias_and_exposes_it_as_display_name()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Basis");

        var install = await _svc.LoadAsync(root, "  My Basis  ");

        Assert.Equal("My Basis", install.Alias);
        Assert.Equal("My Basis", install.DisplayName);
    }

    [Fact]
    public async Task LoadAsync_blank_alias_falls_back_to_folder_name()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Basis");

        var install = await _svc.LoadAsync(root, "   ");

        Assert.Null(install.Alias);
        Assert.Equal("Basis", install.DisplayName);
    }

    [Fact]
    public async Task DeleteFolderAsync_removes_the_folder_including_readonly_files()
    {
        using var t = new TempDir();
        var root = t.CreateDir("Basis");
        var gitObject = t.WriteFile("Basis/.git/objects/ab/cdef123", "packed");
        File.SetAttributes(gitObject, FileAttributes.ReadOnly);   // Git marks packed objects read-only on Windows

        await BasisInstallService.DeleteFolderAsync(root);

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task DeleteFolderAsync_is_a_noop_for_a_missing_folder()
    {
        using var t = new TempDir();
        var missing = t.Combine("never-created");

        await BasisInstallService.DeleteFolderAsync(missing);   // must not throw

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public async Task ToProjectInfo_maps_the_install()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Basis");
        var install = await _svc.LoadAsync(root);

        var info = _svc.ToProjectInfo(install);

        Assert.Equal(install.UnityProjectPath, info.Path);
        Assert.Equal(install.Name, info.Name);
        Assert.Equal(install.UnityVersion, info.UnityVersion);
        Assert.Same(install.Manifest, info.Manifest);
    }
}
