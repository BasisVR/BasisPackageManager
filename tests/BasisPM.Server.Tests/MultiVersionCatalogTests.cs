using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Server.Models;
using BasisPM.Server.Services;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class MultiVersionCatalogTests
{
    private sealed class FakeSource : IPackageVersionSource
    {
        public List<RepoTag>? Tags { get; set; } = new();
        public Dictionary<string, PackageJsonLookup> Manifests { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = new();
        public bool? Exists { get; set; } = true;

        public Task<IReadOnlyList<RepoTag>?> ListTagsAsync(UpmGitUrl repo, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RepoTag>?>(Tags);

        public Task<PackageJsonLookup> ReadPackageJsonAsync(UpmGitUrl repo, string? gitRef, bool isTag, CancellationToken ct = default)
        {
            var key = (isTag ? "tag:" : "ref:") + (gitRef ?? "HEAD");
            lock (Reads) Reads.Add(key);
            return Task.FromResult(Manifests.TryGetValue(key, out var lookup) ? lookup : PackageJsonLookup.Missing);
        }

        public Task<bool?> RepoExistsAsync(UpmGitUrl repo, CancellationToken ct = default) => Task.FromResult(Exists);

        public void Add(string key, string json) => Manifests[key] = new PackageJsonLookup(true, PackageJsonFile.Parse(json));
    }

    private static RegistryPackage Package(string gitUrl = "https://github.com/someone/thing.git") => new()
    {
        Id = "com.someone.thing",
        Name = "Thing",
        Description = "Curated description",
        Author = "someone",
        GitUrl = gitUrl,
        Version = "9.9.9",
        Category = "Tools",
        Stars = 5,
        Dependencies = new Dictionary<string, string> { ["com.someone.base"] = "1.0.0" },
    };

    private static string Manifest(string version, string? unity = null, string extra = "") =>
        $$"""{ "name": "com.someone.thing", "displayName": "Thing {{version}}", "version": "{{version}}", "description": "From {{version}}"{{(unity is null ? "" : $", \"unity\": \"{unity}\"")}}{{extra}} }""";

    [Fact]
    public async Task Crawls_semver_tags_newest_first_and_reads_package_json_at_each_tag()
    {
        var source = new FakeSource
        {
            Tags = new List<RepoTag> { new("v1.0.0", "aaa"), new("v1.2.0", "ccc"), new("nightly", "zzz"), new("v1.1.0", "bbb"), new("1.1.0", "dup") },
        };
        source.Add("tag:v1.0.0", Manifest("1.0.0", "2022.3"));
        source.Add("tag:v1.1.0", Manifest("1.1.0", "6000.0", """, "unityRelease": "10f1", "basis": { "from": "2026-09-16" }"""));
        source.Add("tag:v1.2.0", Manifest("1.2.0", "6000.2"));

        var versions = await new PackageVersionCrawler(source).CrawlAsync(Package());

        Assert.Equal(new[] { "1.2.0", "1.1.0", "1.0.0" }, versions.Select(v => v.Version).ToArray());
        Assert.Equal(new[] { "v1.2.0", "v1.1.0", "v1.0.0" }, versions.Select(v => v.Ref).ToArray());
        Assert.Equal(new[] { "ccc", "bbb", "aaa" }, versions.Select(v => v.Commit).ToArray());
        Assert.Equal("10f1", versions[1].UnityRelease);
        Assert.Equal("2026-09-16", versions[1].Basis!.From);
        Assert.DoesNotContain("tag:nightly", source.Reads);
    }

    [Fact]
    public async Task Keeps_only_the_newest_tags_up_to_the_limit()
    {
        var source = new FakeSource { Tags = Enumerable.Range(1, 30).Select(i => new RepoTag($"v1.{i}.0", null)).ToList() };
        foreach (var tag in source.Tags) source.Add("tag:" + tag.Name, Manifest(tag.Name[1..]));

        var versions = await new PackageVersionCrawler(source, maxVersions: 5).CrawlAsync(Package());

        Assert.Equal(new[] { "1.30.0", "1.29.0", "1.28.0", "1.27.0", "1.26.0" }, versions.Select(v => v.Version).ToArray());
        Assert.Equal(5, source.Reads.Count);
    }

    [Fact]
    public async Task Tags_without_this_package_are_left_out()
    {
        var source = new FakeSource { Tags = new List<RepoTag> { new("v2.0.0", null), new("v1.22", null), new("v1.0.0", null) } };
        source.Add("tag:v2.0.0", """{ "name": "com.someone.other", "version": "2.0.0" }""");
        source.Add("tag:v1.0.0", Manifest("1.0.0"));

        var versions = await new PackageVersionCrawler(source).CrawlAsync(Package());

        Assert.Equal(new[] { "1.0.0" }, versions.Select(v => v.Version).ToArray());
    }

    [Fact]
    public async Task Without_tags_the_crawl_is_empty_so_generate_falls_back_to_one_version()
    {
        var offline = new FakeSource { Tags = null };
        Assert.Empty(await new PackageVersionCrawler(offline).CrawlAsync(Package()));

        var pinned = new FakeSource { Tags = new List<RepoTag> { new("v1.0.0", null) } };
        pinned.Add("tag:v1.0.0", Manifest("1.0.0"));
        Assert.Empty(await new PackageVersionCrawler(pinned).CrawlAsync(Package("https://github.com/someone/thing.git#main")));
    }

    [Fact]
    public async Task ReadCurrent_reads_the_default_branch_or_the_pinned_ref()
    {
        var source = new FakeSource();
        source.Add("ref:HEAD", Manifest("1.3.0-beta.1", "6000.0"));
        source.Add("ref:main", Manifest("1.4.0"));

        var head = await new PackageVersionCrawler(source).ReadCurrentAsync(Package());
        var pinned = await new PackageVersionCrawler(source).ReadCurrentAsync(Package("https://github.com/someone/thing.git#main"));

        Assert.Equal("1.3.0-beta.1", head!.Version);
        Assert.Null(head.Ref);
        Assert.Equal("6000.0", head.Unity);
        Assert.Equal("1.4.0", pinned!.Version);
        Assert.Equal("main", pinned.Ref);
    }

    [Fact]
    public void Dependencies_merge_unity_and_vpm_and_drop_values_the_app_cannot_resolve()
    {
        var manifest = PackageJsonFile.Parse("""
        {
          "name": "com.someone.thing",
          "dependencies": { "com.unity.ugui": "2.0.0", "com.someone.base": "1.2.0" },
          "vpmDependencies": { "com.someone.base": "9.9.9", "com.vrchat.base": "3.5.x", "com.someone.tools": ">=1.0.0", "com.someone.odd": "latest" },
          "gitDependencies": { "com.someone.git": "https://github.com/someone/git.git#v1.0.0", "com.evil.ssh": "git@github.com:evil/x.git", "Bad Id": "https://github.com/x/y.git" }
        }
        """)!;

        var dependencies = PackageVersionCrawler.Dependencies(manifest)!;
        var git = PackageVersionCrawler.GitDependencies(manifest)!;

        Assert.Equal("1.2.0", dependencies["com.someone.base"]);
        Assert.Equal(">=1.0.0", dependencies["com.someone.tools"]);
        Assert.Equal("2.0.0", dependencies["com.unity.ugui"]);
        Assert.Equal("3.5.x", dependencies["com.vrchat.base"]);
        Assert.False(dependencies.ContainsKey("com.someone.odd"));
        Assert.Equal(new[] { "com.someone.git" }, git.Keys.ToArray());
    }

    [Fact]
    public void BuildCatalog_pins_older_versions_and_leaves_the_newest_unpinned()
    {
        var p = Package("https://github.com/someone/thing.git?path=Packages/com.someone.thing");
        p.Versions = new List<RegistryVersion>
        {
            new() { Version = "1.0.0", Ref = "v1.0.0", Commit = "aaa", Unity = "2022.3", DisplayName = "Old Thing" },
            new() { Version = "1.2.0", Ref = "v1.2.0", Commit = "ccc", Unity = "6000.2", UnityRelease = "1f1", Basis = new BasisCompatibility { From = "2026-09-16" },
                Dependencies = new Dictionary<string, string> { ["com.unity.ugui"] = "2.0.0", ["com.someone.base"] = "1.1.0" },
                GitDependencies = new Dictionary<string, string> { ["com.someone.git"] = "https://github.com/someone/git.git" } },
            new() { Version = "1.1.0", Ref = "v1.1.0", Commit = "bbb" },
        };

        var versions = PackageStore.BuildCatalog(new[] { p }).Packages["com.someone.thing"].Versions;

        Assert.Equal(new[] { "1.2.0", "1.0.0", "1.1.0" }.OrderBy(v => v), versions.Keys.OrderBy(v => v));
        var newest = versions["1.2.0"];
        Assert.Equal(p.GitUrl, newest.Url);
        Assert.Equal("v1.2.0", newest.Ref);
        Assert.Equal("ccc", newest.Commit);
        Assert.Equal("6000.2", newest.Unity);
        Assert.Equal("1f1", newest.UnityRelease);
        Assert.Equal("2026-09-16", newest.Basis!.From);
        Assert.Equal("Thing", newest.DisplayName);
        Assert.Equal("Curated description", newest.Description);
        Assert.Equal("1.0.0", newest.Dependencies!["com.someone.base"]);
        Assert.Equal("2.0.0", newest.Dependencies["com.unity.ugui"]);
        Assert.Equal("https://github.com/someone/git.git", newest.GitDependencies!["com.someone.git"]);
        Assert.Equal(5, newest.Stars);
        Assert.Equal("Tools", newest.Category);

        var older = versions["1.0.0"];
        Assert.Equal("https://github.com/someone/thing.git?path=Packages/com.someone.thing#v1.0.0", older.Url);
        Assert.Equal("v1.0.0", older.Ref);
        Assert.Equal("aaa", older.Commit);
        Assert.Equal("Old Thing", older.DisplayName);
        Assert.Equal("Curated description", older.Description);
        Assert.Equal("2022.3", older.Unity);
        Assert.Equal(0, older.Stars);
        Assert.Equal("https://github.com/someone/thing.git?path=Packages/com.someone.thing#v1.1.0", versions["1.1.0"].Url);
    }

    [Fact]
    public void BuildCatalog_keeps_one_version_when_nothing_was_crawled()
    {
        var p = Package();

        var version = PackageStore.BuildCatalog(new[] { p }).Packages["com.someone.thing"].Versions.Single();

        Assert.Equal("9.9.9", version.Key);
        Assert.Equal(p.GitUrl, version.Value.Url);
        Assert.Null(version.Value.Ref);
        Assert.Equal("1.0.0", version.Value.Dependencies!["com.someone.base"]);
    }

    [Fact]
    public void Catalog_json_round_trips_through_the_app_model_and_omits_empty_version_fields()
    {
        var p = Package();
        p.Versions = new List<RegistryVersion> { new() { Version = "1.0.0", Ref = "v1.0.0", Basis = new BasisCompatibility { From = "2026-01-01" } }, new() { Version = "0.9.0", Ref = "v0.9.0" } };

        var json = JsonSerializer.Serialize(PackageStore.BuildCatalog(new[] { p }), RegistrySite.OutputOptions);
        var catalog = JsonSerializer.Deserialize<Catalog>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal("v0.9.0", catalog.Packages["com.someone.thing"].Versions["0.9.0"].Ref);
        Assert.Equal("2026-01-01", catalog.Packages["com.someone.thing"].Versions["1.0.0"].Basis!.From);
        Assert.DoesNotContain("\"unityRelease\"", json);
        Assert.DoesNotContain("\"gitDependencies\"", json);
        Assert.DoesNotContain("\"commit\"", json);
    }

    [Fact]
    public void Packages_json_versions_never_carry_an_id_line_the_website_stats_job_keys_on()
    {
        var p = Package();
        p.Versions = new List<RegistryVersion> { new() { Version = "1.0.0", Ref = "v1.0.0", Commit = "abc" } };

        var json = JsonSerializer.Serialize(new[] { p }, RegistrySite.OutputOptions);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"id\":"));
        Assert.Contains("\"versions\"", json);
    }

    [Fact]
    public void A_curator_reviewed_commit_marks_only_the_version_it_was_reviewed_at()
    {
        var package = Package();
        package.Versions = new List<RegistryVersion>
        {
            new() { Version = "1.0.0", Ref = "v1.0.0", Commit = "aaa" },
            new() { Version = "1.1.0", Ref = "v1.1.0", Commit = "bbb" },
        };
        package.ReviewedCommit = "aaa";

        var versions = PackageStore.BuildCatalog(new[] { package }).Packages["com.someone.thing"].Versions;

        Assert.Equal("aaa", versions["1.0.0"].ReviewedCommit);
        Assert.Null(versions["1.1.0"].ReviewedCommit);
        Assert.Equal("bbb", versions["1.1.0"].Commit);
    }

    [Fact]
    public void A_reviewed_commit_that_matches_no_tag_goes_on_the_newest_version()
    {
        var package = Package();
        package.Versions = new List<RegistryVersion> { new() { Version = "1.0.0", Ref = "v1.0.0", Commit = "aaa" } };
        package.ReviewedCommit = "fff";

        var versions = PackageStore.BuildCatalog(new[] { package }).Packages["com.someone.thing"].Versions;

        Assert.Equal("fff", versions["1.0.0"].ReviewedCommit);
    }
}
