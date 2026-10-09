using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisDevStoreTests
{
    private static BasisDevSidecar Sidecar(string id, string folder = ".basisdev/com.x") => new()
    {
        Package = id,
        Folder = folder,
        Upstream = new BasisDevUpstream { Url = "https://github.com/o/r.git", Ref = "main", Path = "Packages/com.x", Commit = "abc1234" },
        Manifest = new BasisDevManifestLine { Original = "https://github.com/o/r.git?path=Packages/com.x#main", Mounted = "file:../.basisdev/com.x/Packages/com.x" },
    };

    [Fact]
    public void Write_then_Read_round_trips_the_upstream()
    {
        using var t = new TempDir();
        BasisDevStore.Write(t.Path, Sidecar("com.x"));

        var read = BasisDevStore.Read(t.Path, "com.x");

        Assert.NotNull(read);
        Assert.Equal(".basisdev/com.x", read!.Folder);
        Assert.Equal("https://github.com/o/r.git", read.Upstream.Url);
        Assert.Equal("main", read.Upstream.Ref);
        Assert.Equal("Packages/com.x", read.Upstream.Path);
        Assert.Equal("abc1234", read.Upstream.Commit);
        Assert.Equal("https://github.com/o/r.git?path=Packages/com.x#main", read.Manifest.Original);
        Assert.Equal(BasisDevSidecar.CurrentFormat, read.Format);
        Assert.True(File.Exists(t.Combine(".basisdev/com.x.json")));
    }

    [Fact]
    public void Sidecar_json_uses_camel_case_and_lf()
    {
        using var t = new TempDir();
        BasisDevStore.Write(t.Path, Sidecar("com.x"));
        var text = File.ReadAllText(BasisDevStore.SidecarPath(t.Path, "com.x"));

        Assert.Contains("\"upstream\": {", text);
        Assert.Contains("\"package\": \"com.x\"", text);
        Assert.DoesNotContain("manifestUrl", text);
        Assert.DoesNotContain("\r", text);
    }

    [Fact]
    public void List_skips_sidecars_that_describe_another_package()
    {
        using var t = new TempDir();
        BasisDevStore.Write(t.Path, Sidecar("com.x"));
        t.WriteFile(".basisdev/com.y.json", """{ "package": "com.z", "folder": ".basisdev/com.y" }""");
        t.WriteFile(".basisdev/notes.txt", "not a sidecar");

        var all = BasisDevStore.List(t.Path);

        Assert.Equal("com.x", Assert.Single(all).Package);
    }

    [Fact]
    public void Unreadable_sidecar_is_ignored_and_kept_aside()
    {
        using var t = new TempDir();
        var path = t.WriteFile(".basisdev/com.x.json", "{ not json");

        Assert.Null(BasisDevStore.Read(t.Path, "com.x"));
        Assert.True(File.Exists(path + ".corrupt"));
    }

    [Theory]
    [InlineData(".basisdev/com.x", true)]
    [InlineData("Packages/com.x", true)]
    [InlineData("Assets/com.x", false)]
    [InlineData(".basisdev/../../escape", false)]
    [InlineData(".basisdev", false)]
    [InlineData(".basisdev/a/b", false)]
    [InlineData("C:/elsewhere/com.x", false)]
    [InlineData("", false)]
    public void ResolveFolder_only_accepts_project_clone_folders(string folder, bool ok)
    {
        using var t = new TempDir();
        Assert.Equal(ok, BasisDevStore.ResolveFolder(t.Path, folder) is not null);
    }

    [Theory]
    [InlineData("com.x", true)]
    [InlineData("dev.hai-vr.basis.comms", true)]
    [InlineData("../com.x", false)]
    [InlineData("a/b", false)]
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    public void IsValidId_rejects_paths(string id, bool ok) => Assert.Equal(ok, BasisDevStore.IsValidId(id));

    [Fact]
    public void Write_refuses_a_folder_outside_the_project()
    {
        using var t = new TempDir();
        Assert.Throws<ArgumentException>(() => BasisDevStore.Write(t.Path, Sidecar("com.x", "../outside/com.x")));
        Assert.False(Directory.Exists(t.Combine(".basisdev")) && File.Exists(t.Combine(".basisdev/com.x.json")));
    }

    [Fact]
    public void Delete_removes_only_that_sidecar()
    {
        using var t = new TempDir();
        BasisDevStore.Write(t.Path, Sidecar("com.x"));
        BasisDevStore.Write(t.Path, Sidecar("com.y", ".basisdev/com.y"));

        Assert.True(BasisDevStore.Delete(t.Path, "com.x"));
        Assert.False(BasisDevStore.Delete(t.Path, "com.x"));
        Assert.Null(BasisDevStore.Read(t.Path, "com.x"));
        Assert.NotNull(BasisDevStore.Read(t.Path, "com.y"));
    }

    [Fact]
    public void ListClones_returns_only_folders_with_their_own_git_directory()
    {
        using var t = new TempDir();
        t.CreateDir(".basisdev/com.clone/.git");
        t.CreateDir(".basisdev/com.plain");
        t.WriteFile(".basisdev/com.file/.git", "gitdir: elsewhere");

        var clones = BasisDevStore.ListClones(t.Path);

        Assert.Equal("com.clone", Assert.Single(clones).Key);
    }

    [Fact]
    public void Upstream_manifest_url_rebuilds_the_upm_line()
    {
        var upstream = new BasisDevUpstream { Url = "https://github.com/o/r.git", Ref = "v1", Path = "Packages/com.x" };
        Assert.Equal("https://github.com/o/r.git?path=Packages/com.x#v1", upstream.ManifestUrl);
        Assert.Null(new BasisDevUpstream { Url = "" }.ManifestUrl);
    }
}
