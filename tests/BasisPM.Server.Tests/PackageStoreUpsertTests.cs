using System.Text.Json;
using BasisPM.Server.Models;
using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class PackageStoreUpsertTests
{
    private static PackageStore EmptyStore(TempDir t)
    {
        var seed = t.WriteFile("seed/packages.json", "[]");
        return new PackageStore(t.Combine("data"), seed);
    }

    private static RegistrySubmission Sub(
        string? id = "com.test.pkg", string? name = "Test Package",
        string? gitUrl = "https://github.com/someone/repo.git")
        => new() { Id = id ?? "", Name = name ?? "", GitUrl = gitUrl };

    [Fact]
    public void Upsert_accepts_a_valid_submission_and_persists_it()
    {
        using var t = new TempDir();
        var store = EmptyStore(t);

        var pkg = store.Upsert(Sub());

        Assert.Equal("com.test.pkg", pkg.Id);
        Assert.Equal("community", pkg.Source);
        Assert.Equal("📦", pkg.Icon);
        Assert.Single(store.All());

        var reopened = new PackageStore(t.Combine("data"), t.Combine("seed/packages.json"));
        Assert.NotNull(reopened.Get("com.test.pkg"));
    }

    [Fact]
    public void Upsert_applies_defaults_for_blank_optional_fields()
    {
        using var t = new TempDir();
        var pkg = EmptyStore(t).Upsert(Sub());

        Assert.Equal("Community", pkg.Author);
        Assert.Equal("Misc", pkg.Category);
        Assert.Equal("1.0.0", pkg.Version);
    }

    [Fact]
    public void Upsert_forces_community_even_for_a_basisvr_url()
    {
        using var t = new TempDir();
        var pkg = EmptyStore(t).Upsert(Sub(gitUrl: "https://github.com/BasisVR/thing.git"));
        Assert.Equal("community", pkg.Source);
    }

    [Fact]
    public void Upsert_cleans_and_caps_tags()
    {
        using var t = new TempDir();
        var sub = Sub();
        sub.Tags = new List<string> { "  spaced  ", "", "   ", new string('x', 100) , "ok" };

        var pkg = EmptyStore(t).Upsert(sub);

        Assert.Contains("spaced", pkg.Tags);
        Assert.Contains("ok", pkg.Tags);
        Assert.DoesNotContain(pkg.Tags, tg => tg.Length > 64);
        Assert.DoesNotContain("", pkg.Tags);
    }

    [Theory]
    [InlineData(null, "Name", "https://github.com/o/r.git", "id is required")]
    [InlineData("com.x", null, "https://github.com/o/r.git", "name is required")]
    [InlineData("com.x", "Name", null, "Provide a gitUrl")]
    [InlineData("bad/id", "Name", "https://github.com/o/r.git", "may contain only")]
    [InlineData("com.x", "Name", "https://evil.example.com/r.git", "github.com or gitlab.com")]
    [InlineData("com.x", "Name", "git@github.com:o/r.git", "github.com or gitlab.com")]
    [InlineData("com.x", "Name", "http://github.com/o/r.git", "github.com or gitlab.com")]
    public void Upsert_rejects_bad_required_fields(string? id, string? name, string? gitUrl, string expectedFragment)
    {
        using var t = new TempDir();
        var ex = Assert.Throws<ArgumentException>(() => EmptyStore(t).Upsert(Sub(id, name, gitUrl)));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void Upsert_rejects_a_non_web_repo_url()
    {
        using var t = new TempDir();
        var sub = Sub();
        sub.RepoUrl = "javascript:alert(1)";
        var ex = Assert.Throws<ArgumentException>(() => EmptyStore(t).Upsert(sub));
        Assert.Contains("repoUrl", ex.Message);
    }

    [Theory]
    [InlineData("discord")]
    [InlineData("donate")]
    [InlineData("authorUrl")]
    public void Upsert_rejects_non_web_links(string which)
    {
        using var t = new TempDir();
        var sub = Sub();
        switch (which)
        {
            case "discord": sub.Discord = "ftp://x"; break;
            case "donate": sub.Donate = "ftp://x"; break;
            case "authorUrl": sub.AuthorUrl = "ftp://x"; break;
        }
        var ex = Assert.Throws<ArgumentException>(() => EmptyStore(t).Upsert(sub));
        Assert.Contains(which, ex.Message);
    }

    [Fact]
    public void Upsert_rejects_overlong_fields()
    {
        using var t = new TempDir();
        var sub = Sub(name: new string('n', 2001));
        var ex = Assert.Throws<ArgumentException>(() => EmptyStore(t).Upsert(sub));
        Assert.Contains("length limit", ex.Message);
    }

    [Fact]
    public void Upsert_rejects_too_many_dependencies()
    {
        using var t = new TempDir();
        var sub = Sub();
        sub.Dependencies = Enumerable.Range(0, 129).ToDictionary(i => $"com.dep{i}", _ => "1.0.0");
        var ex = Assert.Throws<ArgumentException>(() => EmptyStore(t).Upsert(sub));
        Assert.Contains("Too many dependencies", ex.Message);
    }

    [Fact]
    public void Upsert_is_create_only_and_rejects_duplicate_ids()
    {
        using var t = new TempDir();
        var store = EmptyStore(t);
        store.Upsert(Sub(id: "com.dup"));

        var ex = Assert.Throws<InvalidOperationException>(() => store.Upsert(Sub(id: "com.dup")));
        Assert.Contains("already exists", ex.Message);
    }
}
