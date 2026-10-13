using System.Text.Json;
using BasisPM.Server.Models;
using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class SeedFolderTests
{
    private static string Package(string id, string name, string gitUrl = "https://github.com/someone/repo.git") =>
        JsonSerializer.Serialize(new { id, name, description = "d", author = "a", gitUrl });

    [Fact]
    public void Loads_one_file_per_package_sorted_by_id()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.b.two.json", Package("com.b.two", "Two"));
        t.WriteFile("seed/packages/com.a.one.json", Package("com.a.one", "One", "https://github.com/BasisVR/one.git"));

        var packages = PackageStore.LoadSeed(t.Combine("seed"));

        Assert.Equal(new[] { "com.a.one", "com.b.two" }, packages.Select(p => p.Id).ToArray());
        Assert.Equal("official", packages[0].Source);
        Assert.Equal("community", packages[1].Source);
    }

    [Fact]
    public void Legacy_packages_json_is_merged_and_a_per_package_file_wins()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", Package("com.a.one", "From its own file"));
        t.WriteFile("seed/packages.json", JsonSerializer.Serialize(new[]
        {
            new RegistryPackage { Id = "com.a.one", Name = "From the legacy list", GitUrl = "https://github.com/x/a.git" },
            new RegistryPackage { Id = "com.c.three", Name = "Legacy only", GitUrl = "https://github.com/x/c.git" },
        }));

        var packages = PackageStore.LoadSeed(t.Combine("seed"));

        Assert.Equal(2, packages.Count);
        Assert.Equal("From its own file", packages.Single(p => p.Id == "com.a.one").Name);
        Assert.Contains(packages, p => p.Id == "com.c.three");
    }

    [Fact]
    public void A_legacy_file_path_still_loads_the_package_folder_next_to_it()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", Package("com.a.one", "One"));
        var legacy = t.WriteFile("seed/packages.json", "[]");

        Assert.Single(PackageStore.LoadSeed(legacy));
    }

    [Fact]
    public void Files_that_are_misnamed_or_broken_are_skipped()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", Package("com.a.one", "One"));
        t.WriteFile("seed/packages/com.wrong.name.json", Package("com.other.id", "Misnamed"));
        t.WriteFile("seed/packages/com.broken.json", "{ not json");

        Assert.Equal(new[] { "com.a.one" }, PackageStore.LoadSeed(t.Combine("seed")).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void A_file_without_an_id_takes_its_file_name()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", JsonSerializer.Serialize(new { name = "One", gitUrl = "https://github.com/x/a.git" }));

        Assert.Equal("com.a.one", PackageStore.LoadSeed(t.Combine("seed")).Single().Id);
    }

    [Fact]
    public void The_store_reads_a_seed_folder()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", Package("com.a.one", "One"));

        var store = new PackageStore(t.Combine("data"), t.Combine("seed"));

        Assert.NotNull(store.Get("com.a.one"));
    }

    [Fact]
    public void The_committed_seed_is_one_valid_file_per_package()
    {
        var folder = TestServerRoot.SeedPath("");
        var result = SeedValidator.Load(folder);

        Assert.NotEmpty(result.Packages);
        Assert.Empty(result.Issues.Where(i => i.Error));
        Assert.False(File.Exists(Path.Combine(folder, PackageStore.LegacySeedFile)));
        foreach (var (package, _) in result.Packages)
            Assert.Empty(SeedValidator.CheckEntry(package, package.Id, Path.Combine(TestServerRoot.ServerSourceDir, "wwwroot", "icons")).Where(i => i.Error));
    }

    [Fact]
    public void Checks_json_is_copied_byte_for_byte()
    {
        using var t = new TempDir();
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'\r', (byte)'\n', (byte)'}', 0x20 };
        Directory.CreateDirectory(t.Combine("seed"));
        File.WriteAllBytes(t.Combine("seed", RegistrySite.ChecksFile), bytes);

        Assert.True(RegistrySite.CopyChecks(t.Combine("seed"), t.Combine("out")));
        Assert.Equal(bytes, File.ReadAllBytes(t.Combine("out", RegistrySite.ChecksFile)));
    }

    [Fact]
    public void Missing_checks_json_is_skipped()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(t.Combine("seed"));

        Assert.False(RegistrySite.CopyChecks(t.Combine("seed"), t.Combine("out")));
        Assert.False(File.Exists(t.Combine("out", RegistrySite.ChecksFile)));
    }

    [Fact]
    public void Package_lists_fall_back_to_the_legacy_bundles_file()
    {
        using var t = new TempDir();
        t.WriteFile("seed/bundles.json", "[]");
        Assert.EndsWith("bundles.json", RegistrySite.PackageListsFile(t.Combine("seed")));

        t.WriteFile("seed/packagelists.json", "[]");
        Assert.EndsWith("packagelists.json", RegistrySite.PackageListsFile(t.Combine("seed")));
    }
}
