using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisPackagesStoreTests
{
    [Fact]
    public void Entries_are_written_to_the_manifest_and_lock_and_read_back_by_id()
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");

        BasisPackagesStore.Set(project, "com.zeta", new BasisPackageEntry { Version = "2.0.0", Url = "https://github.com/o/zeta.git", Ref = "v2.0.0", Commit = "abc1234" });
        BasisPackagesStore.Set(project, "com.alpha", new BasisPackageEntry { Url = "https://gitlab.com/g/alpha.git", Path = "Packages/com.alpha" });

        Assert.Equal(Path.Combine(project, "bpm-manifest.json"), BasisPackagesStore.FilePath(project));
        Assert.Equal("""
            {
              "format": 1,
              "packages": {
                "com.alpha": {
                  "version": "https://gitlab.com/g/alpha.git?path=Packages/com.alpha"
                },
                "com.zeta": {
                  "version": "https://github.com/o/zeta.git#v2.0.0"
                }
              }
            }

            """.ReplaceLineEndings("\n"), File.ReadAllText(BasisPackagesStore.FilePath(project)));
        Assert.Equal("""
            {
              "format": 1,
              "packages": {
                "com.alpha": {
                  "version": "https://gitlab.com/g/alpha.git?path=Packages/com.alpha",
                  "requested": "https://gitlab.com/g/alpha.git?path=Packages/com.alpha",
                  "depth": 0
                },
                "com.zeta": {
                  "version": "https://github.com/o/zeta.git#abc1234",
                  "requested": "https://github.com/o/zeta.git#v2.0.0",
                  "packageVersion": "2.0.0",
                  "depth": 0
                }
              }
            }

            """.ReplaceLineEndings("\n"), File.ReadAllText(BpmStore.LockPath(project)));
        var zeta = BasisPackagesStore.Find(project, "COM.ZETA")!;
        Assert.Equal("v2.0.0", zeta.Ref);
        Assert.Equal("abc1234", zeta.Commit);
        Assert.Equal("2.0.0", zeta.Version);
        Assert.Equal("Packages/com.alpha", BasisPackagesStore.Find(project, "com.alpha")!.Path);
        Assert.Equal(new[] { "com.alpha", "com.zeta" }, BasisPackagesStore.Read(project).Keys.Order());
    }

    [Fact]
    public void Removing_the_last_entry_deletes_both_files()
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");
        BasisPackagesStore.Set(project, "com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git" });

        Assert.True(BasisPackagesStore.Remove(project, "COM.X"));
        Assert.False(File.Exists(BasisPackagesStore.FilePath(project)));
        Assert.False(File.Exists(BpmStore.LockPath(project)));
        Assert.False(BasisPackagesStore.Remove(project, "com.x"));
        Assert.Empty(BasisPackagesStore.Read(project));
    }

    [Fact]
    public void An_old_basispackages_file_is_read_and_replaced_on_the_first_change()
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");
        var legacy = t.WriteFile("Proj/Packages/basispackages.json", """{ "format": 1, "note": "kept", "packages": { "com.x": { "version": "1.0.0", "url": "https://github.com/o/x.git", "ref": "v1", "commit": "abc1234", "server": true } } }""");

        var read = BasisPackagesStore.Find(project, "com.x")!;
        Assert.Equal(("v1", "abc1234", "1.0.0"), (read.Ref, read.Commit, read.Version));

        BasisPackagesStore.Set(project, "com.y", new BasisPackageEntry { Url = "https://github.com/o/y.git" });

        Assert.False(File.Exists(legacy));
        var text = File.ReadAllText(BasisPackagesStore.FilePath(project));
        Assert.Contains("\"note\": \"kept\"", text);
        Assert.Contains("\"server\": true", text);
        Assert.Contains("\"version\": \"https://github.com/o/x.git#v1\"", text);
        Assert.Contains("\"version\": \"https://github.com/o/x.git#abc1234\"", File.ReadAllText(BpmStore.LockPath(project)));
        Assert.Equal(2, BasisPackagesStore.Read(project).Count);
    }

    [Fact]
    public void Fields_from_other_versions_survive_an_update()
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/bpm-manifest.json", """{ "format": 1, "later": 3, "packages": { "com.x": { "version": "https://github.com/o/x.git", "enabled": "yes", "server": true } } }""");

        BasisPackagesStore.Set(project, "com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Ref = "v2" });

        var text = File.ReadAllText(BasisPackagesStore.FilePath(project));
        Assert.Contains("\"later\": 3", text);
        Assert.Contains("\"server\": true", text);
        Assert.Contains("\"enabled\": \"yes\"", text);
        Assert.Contains("#v2", text);
    }

    [Fact]
    public void Disabled_packages_are_not_listed()
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/bpm-manifest.json", """{ "packages": { "com.x": { "version": "https://github.com/o/x.git", "enabled": "no" }, "com.y": { "version": "https://github.com/o/y.git" } } }""");

        Assert.Equal(new[] { "com.y" }, BasisPackagesStore.Read(project).Keys);
    }

    [Theory]
    [InlineData("Packages/basispackages.json")]
    [InlineData("bpm-manifest.json")]
    public void A_file_with_a_merge_conflict_is_never_overwritten(string file)
    {
        using var t = new TempDir("basispackages");
        var project = t.CreateDir("Proj");
        const string conflicted = "{\n<<<<<<< HEAD\n  \"packages\": {}\n=======\n  \"packages\": { \"com.y\": { \"url\": \"https://github.com/o/y.git\" } }\n>>>>>>> theirs\n}\n";
        var path = t.WriteFile("Proj/" + file, conflicted);

        Assert.Empty(BasisPackagesStore.Read(project));
        Assert.Throws<InvalidDataException>(() => BasisPackagesStore.Set(project, "com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git" }));
        Assert.Throws<InvalidDataException>(() => BasisPackagesStore.Remove(project, "com.y"));
        Assert.Equal(conflicted, File.ReadAllText(path));
    }

    [Fact]
    public void Entries_that_escape_the_clone_or_carry_bad_refs_are_not_trusted()
    {
        Assert.Null(BasisPackagesStore.SidecarFor("com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Path = "../../outside" }));
        Assert.Null(BasisPackagesStore.SidecarFor("com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Ref = "--upload-pack=evil" }));
        Assert.Null(BasisPackagesStore.SidecarFor("../escape", new BasisPackageEntry { Url = "https://github.com/o/x.git" }));
        Assert.Throws<ArgumentException>(() => BasisPackagesStore.Set(Path.GetTempPath(), "../escape", new BasisPackageEntry { Url = "https://github.com/o/x.git" }));

        var sidecar = BasisPackagesStore.SidecarFor("com.x", new BasisPackageEntry { Url = "https://github.com/o/x.git", Ref = "v1", Path = "Packages/com.x", Commit = "abc1234" })!;

        Assert.Equal(".basisdev/com.x", sidecar.Folder);
        Assert.Equal("https://github.com/o/x.git?path=Packages/com.x#v1", sidecar.Manifest.Original);
        Assert.Equal("file:../.basisdev/com.x/Packages/com.x", sidecar.Manifest.Mounted);
        Assert.Equal("abc1234", sidecar.Upstream.Commit);
    }

    [Fact]
    public void The_entry_version_comes_from_the_package_json()
    {
        using var t = new TempDir("basispackages");
        var root = t.CreateDir("pkg");
        t.WriteFile("pkg/package.json", """{ "name": "com.x", "version": "3.1.4" }""");

        var entry = BasisPackagesStore.EntryFor(new BasisDevUpstream { Url = "https://github.com/o/x.git", Ref = " ", Commit = "def5678" }, root);

        Assert.Equal("3.1.4", entry.Version);
        Assert.Null(entry.Ref);
        Assert.Equal("def5678", entry.Commit);
        Assert.Null(BasisPackagesStore.EntryFor(new BasisDevUpstream { Url = "https://github.com/o/x.git" }, t.Combine("missing")).Version);
    }
}
