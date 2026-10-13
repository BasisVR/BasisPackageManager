using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Server.Models;
using BasisPM.Server.Services;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class SeedValidatorTests
{
    private sealed class FakeSource(bool? exists, UpmPackageJson? package, bool reachable = true) : IPackageVersionSource
    {
        public Task<IReadOnlyList<RepoTag>?> ListTagsAsync(UpmGitUrl repo, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RepoTag>?>(null);
        public Task<PackageJsonLookup> ReadPackageJsonAsync(UpmGitUrl repo, string? gitRef, bool isTag, CancellationToken ct = default) =>
            Task.FromResult(reachable ? new PackageJsonLookup(true, package) : PackageJsonLookup.Unreachable);
        public Task<bool?> RepoExistsAsync(UpmGitUrl repo, CancellationToken ct = default) => Task.FromResult(exists);
    }

    private static RegistryPackage Valid() => new()
    {
        Id = "com.someone.thing",
        Name = "Thing",
        Description = "Does a thing.",
        Author = "someone",
        GitUrl = "https://github.com/someone/thing.git",
        License = "MIT",
    };

    private static List<SeedIssue> Check(RegistryPackage p, string? icons = null) => SeedValidator.CheckEntry(p, "seed/packages/x.json", icons).ToList();

    [Fact]
    public void A_complete_entry_passes()
    {
        Assert.Empty(Check(Valid()));
    }

    [Theory]
    [InlineData("Com.Someone.Thing")]
    [InlineData("comsomeonething")]
    [InlineData("com.someone.thing/../x")]
    [InlineData(".com.someone")]
    public void Bad_ids_are_errors(string id)
    {
        var p = Valid();
        p.Id = id;
        Assert.Contains(Check(p), i => i.Error && i.Message.StartsWith("id "));
    }

    [Fact]
    public void A_two_part_id_is_only_a_warning()
    {
        var p = Valid();
        p.Id = "com.thing";
        var issue = Assert.Single(Check(p));
        Assert.False(issue.Error);
    }

    [Theory]
    [InlineData("https://evil.example.com/x.git")]
    [InlineData("git@github.com:someone/thing.git")]
    [InlineData("http://github.com/someone/thing.git")]
    [InlineData("https://github.com/someone/thing.git?path=../../etc")]
    public void Git_urls_must_be_hosted_https_without_escaping_paths(string url)
    {
        var p = Valid();
        p.GitUrl = url;
        Assert.Contains(Check(p), i => i.Error && i.Message.StartsWith("gitUrl"));
    }

    [Fact]
    public void Required_text_and_web_links_are_checked()
    {
        var p = Valid();
        p.Name = "";
        p.Description = " ";
        p.Discord = "javascript:alert(1)";
        p.Version = "one";
        p.Dependencies = new Dictionary<string, string> { ["com.x.y"] = "not a range" };

        var messages = Check(p).Where(i => i.Error).Select(i => i.Message).ToList();

        Assert.Contains("name is required.", messages);
        Assert.Contains("description is required.", messages);
        Assert.Contains("discord has to be an http(s) URL.", messages);
        Assert.Contains(messages, m => m.StartsWith("version"));
        Assert.Contains(messages, m => m.StartsWith("dependency \"com.x.y\""));
    }

    [Fact]
    public void An_image_must_exist_in_icons_or_live_in_the_packages_own_repo()
    {
        using var t = new TempDir();
        t.WriteFile("icons/com.someone.thing.png", "png");
        var p = Valid();

        p.Image = "icons/com.someone.thing.png";
        Assert.Empty(Check(p, t.Combine("icons")));

        p.Image = "icons/missing.png";
        Assert.Contains(Check(p, t.Combine("icons")), i => i.Error && i.Message.StartsWith("image"));

        p.Image = "https://raw.githubusercontent.com/someone/thing/main/icon.png";
        Assert.Empty(Check(p, t.Combine("icons")));

        p.Image = "https://evil.example.com/track.png";
        Assert.Contains(Check(p, t.Combine("icons")), i => i.Error && i.Message.StartsWith("image"));
    }

    [Fact]
    public void Load_reports_misnamed_files_duplicates_generated_fields_and_bad_json()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages/com.a.one.json", JsonSerializer.Serialize(new { id = "com.a.one", name = "One", stars = 4 }));
        t.WriteFile("seed/packages/com.b.two.json", JsonSerializer.Serialize(new { id = "com.a.one", name = "Copy" }));
        t.WriteFile("seed/packages/com.c.three.json", "{ nope");
        t.WriteFile("seed/packagelists.json", "{}");

        var result = SeedValidator.Load(t.Combine("seed"));

        Assert.Single(result.Packages);
        Assert.Contains(result.Issues, i => !i.Error && i.Message.Contains("\"stars\""));
        Assert.Contains(result.Issues, i => i.Error && i.File.EndsWith("com.b.two.json") && i.Message.Contains("has to be named com.a.one.json"));
        Assert.Contains(result.Issues, i => i.Error && i.File.EndsWith("com.b.two.json") && i.Message.Contains("already listed"));
        Assert.Contains(result.Issues, i => i.Error && i.File.EndsWith("com.c.three.json") && i.Message.StartsWith("isn't valid JSON"));
        Assert.Contains(result.Issues, i => i.Error && i.File == "seed/packagelists.json");
    }

    [Fact]
    public void Leftover_legacy_entries_are_flagged_and_still_checked()
    {
        using var t = new TempDir();
        t.WriteFile("seed/packages.json", JsonSerializer.Serialize(new[] { Valid() }));

        var result = SeedValidator.Load(t.Combine("seed"));

        Assert.Single(result.Packages);
        Assert.Contains(result.Issues, i => !i.Error && i.File == "seed/packages.json");
    }

    [Fact]
    public async Task Remote_checks_cover_a_missing_repo_a_wrong_name_and_a_missing_license()
    {
        var p = Valid();
        p.License = null;

        var missingRepo = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(false, null));
        var noManifest = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(true, null));
        var wrongName = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(true, new UpmPackageJson { Name = "com.someone.other", License = "MIT" }));
        var noLicense = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(true, new UpmPackageJson { Name = "com.someone.thing" }));
        var fine = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(true, new UpmPackageJson { Name = "com.someone.thing", License = "MIT" }));
        var offline = await SeedValidator.CheckRemoteAsync(p, "f", new FakeSource(null, null));

        Assert.Contains(missingRepo, i => i.Error && i.Message.Contains("wasn't found"));
        Assert.Contains(noManifest, i => i.Error && i.Message.StartsWith("there is no package.json"));
        Assert.Contains(wrongName, i => i.Error && i.Message.Contains("\"com.someone.other\""));
        Assert.Contains(noLicense, i => i.Error && i.Message.Contains("no license"));
        Assert.Empty(fine);
        Assert.All(offline, i => Assert.False(i.Error));
    }

    [Fact]
    public async Task CheckAsync_offline_runs_without_a_source_and_formats_a_report()
    {
        using var t = new TempDir();
        var p = Valid();
        t.WriteFile("seed/packages/com.someone.thing.json", JsonSerializer.Serialize(new { id = p.Id, name = p.Name, description = p.Description, author = p.Author, gitUrl = p.GitUrl }));

        var result = await SeedValidator.CheckAsync(t.Combine("seed"), null, null);
        var report = SeedValidator.Format(result);

        Assert.Equal(0, result.Errors);
        Assert.Contains("warning  seed/packages/com.someone.thing.json: has no license in the seed", report);
        Assert.EndsWith("Checked 1 package(s): 0 error(s), 1 warning(s).", report);
    }
}
