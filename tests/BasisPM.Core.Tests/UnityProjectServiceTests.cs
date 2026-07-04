using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityProjectServiceTests
{
    private readonly UnityProjectService _svc = new();

    private static string MakeProject(TempDir t, string name, string editorVersion = "6000.0.25f1")
    {
        var root = t.CreateDir(name);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
            $"m_EditorVersion: {editorVersion}\nm_EditorVersionWithRevision: {editorVersion} (abc123)\n");
        return root;
    }

    [Fact]
    public void Detect_accepts_a_project_root()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        var r = _svc.Detect(root);
        Assert.True(r.IsValid);
        Assert.Equal(root, r.ResolvedPath);
        Assert.True(_svc.IsUnityProject(root));
    }

    [Fact]
    public void Detect_resolves_upward_from_a_subfolder()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        var r = _svc.Detect(Path.Combine(root, "Assets"));
        Assert.True(r.IsValid);
        Assert.Equal(root, r.ResolvedPath);
    }

    [Fact]
    public void Detect_resolves_into_a_single_subfolder_project()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        var r = _svc.Detect(t.Path);
        Assert.True(r.IsValid);
        Assert.Equal(root, r.ResolvedPath);
    }

    [Fact]
    public void Detect_fails_on_empty_path()
    {
        var r = _svc.Detect("");
        Assert.False(r.IsValid);
        Assert.Contains("No path", r.Reason);
    }

    [Fact]
    public void Detect_fails_on_missing_folder()
    {
        var r = _svc.Detect(Path.Combine(Path.GetTempPath(), "definitely-not-here-" + Guid.NewGuid().ToString("N")));
        Assert.False(r.IsValid);
        Assert.Contains("does not exist", r.Reason);
    }

    [Fact]
    public void Detect_explains_a_non_unity_folder()
    {
        using var t = new TempDir();
        var plain = t.CreateDir("plain");
        Directory.CreateDirectory(Path.Combine(plain, "Assets"));
        var r = _svc.Detect(plain);
        Assert.False(r.IsValid);
        Assert.Contains("ProjectSettings", r.Reason);
    }

    [Fact]
    public async Task LoadAsync_reads_version_and_manifest()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Basis", "6000.0.25f1");
        t.WriteFile("Basis/Packages/manifest.json",
            """{ "dependencies": { "com.basis.sdk": "https://github.com/BasisVR/Basis.git" } }""");

        var info = await _svc.LoadAsync(root);

        Assert.Equal("Basis", info.Name);
        Assert.Equal("6000.0.25f1", info.UnityVersion);
        Assert.Equal("https://github.com/BasisVR/Basis.git", info.Manifest.Dependencies["com.basis.sdk"]);
    }

    [Fact]
    public async Task LoadAsync_reports_unknown_version_when_line_absent()
    {
        using var t = new TempDir();
        var root = t.CreateDir("Proj");
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"), "something: else\n");

        var info = await _svc.LoadAsync(root);
        Assert.Equal("unknown", info.UnityVersion);
    }

    [Fact]
    public async Task LoadAsync_throws_for_non_project()
    {
        using var t = new TempDir();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.LoadAsync(t.Path));
    }

    [Fact]
    public async Task SaveManifest_then_load_round_trips_dependencies()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        var manifest = new PackageManifest();
        manifest.Dependencies["com.a"] = "1.0.0";
        manifest.Dependencies["com.b"] = "https://github.com/x/b.git";

        await _svc.SaveManifestAsync(root, manifest);
        var reloaded = await _svc.LoadAsync(root);

        Assert.Equal("1.0.0", reloaded.Manifest.Dependencies["com.a"]);
        Assert.Equal("https://github.com/x/b.git", reloaded.Manifest.Dependencies["com.b"]);
    }

    [Fact]
    public async Task SaveManifest_preserves_unknown_top_level_keys()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        t.WriteFile("Proj/Packages/manifest.json",
            """{ "dependencies": { "com.a": "1.0.0" }, "enableLockFile": false, "scopedRegistries": [] }""");

        var info = await _svc.LoadAsync(root);
        info.Manifest.Dependencies["com.new"] = "2.0.0";
        await _svc.SaveManifestAsync(root, info.Manifest);

        var text = await File.ReadAllTextAsync(Path.Combine(root, "Packages", "manifest.json"));
        Assert.Contains("enableLockFile", text);
        Assert.Contains("com.new", text);
    }

    [Fact]
    public void ListEmbeddedPackages_reads_package_json_and_sorts_by_display_name()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        t.WriteFile("Proj/Packages/pkgB/package.json", """{ "name": "com.b", "displayName": "Beta", "version": "2.0.0" }""");
        t.WriteFile("Proj/Packages/pkgA/package.json", """{ "name": "com.a", "displayName": "Alpha", "version": "1.0.0" }""");
        t.CreateDir("Proj/Packages/no-json");

        var packages = _svc.ListEmbeddedPackages(root);

        Assert.Equal(2, packages.Count);
        Assert.Equal("Alpha", packages[0].DisplayName);
        Assert.Equal("Beta", packages[1].DisplayName);
        Assert.Equal("com.a", packages[0].Id);
        Assert.Equal("1.0.0", packages[0].Version);
    }

    [Fact]
    public void ListEmbeddedPackages_falls_back_to_folder_name_without_metadata()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        t.WriteFile("Proj/Packages/com.folderid/package.json", """{ "version": "1.0.0" }""");

        var packages = _svc.ListEmbeddedPackages(root);

        Assert.Single(packages);
        Assert.Equal("com.folderid", packages[0].Id);
        Assert.Equal("com.folderid", packages[0].DisplayName);
    }

    [Fact]
    public void ListEmbeddedPackages_detects_a_git_checkout()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        t.WriteFile("Proj/Packages/com.git/package.json", """{ "name": "com.git", "displayName": "Git Pkg" }""");
        t.CreateDir("Proj/Packages/com.git/.git");

        var pkg = _svc.ListEmbeddedPackages(root).Single();
        Assert.True(pkg.IsGitRepo);
    }

    [Fact]
    public void ListEmbeddedPackages_returns_empty_without_packages_folder()
    {
        using var t = new TempDir();
        var root = MakeProject(t, "Proj");
        Assert.Empty(_svc.ListEmbeddedPackages(root));
    }
}
