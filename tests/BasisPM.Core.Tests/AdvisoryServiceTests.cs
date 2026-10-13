using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class AdvisoryServiceTests
{
    private const string Repo = "https://github.com/example/tool.git";
    private const string BadCommit = "1111111111111111111111111111111111111111";
    private const string GoodCommit = "2222222222222222222222222222222222222222";

    private static PackageAdvisory Advisory(string package = "com.example.tool", string? repository = null, string? severity = null, params string[] commits) => new()
    {
        Id = "A-1",
        Package = package,
        Repository = repository,
        Severity = severity,
        Commits = commits.Length == 0 ? null : commits.ToList(),
        Title = "Steals Discord tokens",
    };

    [Fact]
    public void An_advisory_matches_its_package_from_any_source_unless_it_names_a_repository()
    {
        var anywhere = new[] { Advisory() };
        Assert.NotNull(AdvisoryService.Match(anywhere, "COM.EXAMPLE.TOOL", "https://gitlab.com/someone/fork.git", null));
        Assert.Null(AdvisoryService.Match(anywhere, "com.example.other", Repo, null));

        var oneRepo = new[] { Advisory(repository: "https://github.com/Example/Tool") };
        Assert.NotNull(AdvisoryService.Match(oneRepo, "com.example.tool", Repo + "?path=Packages/x#v1", null));
        Assert.Null(AdvisoryService.Match(oneRepo, "com.example.tool", "https://github.com/example/tool-fork.git", null));
        Assert.NotNull(AdvisoryService.Match(oneRepo, "com.example.tool", null, null));
    }

    [Fact]
    public void Commit_lists_only_flag_those_commits_and_unknown_commits_follow_the_caller()
    {
        var advisories = new[] { Advisory(commits: BadCommit[..10]) };

        Assert.NotNull(AdvisoryService.Match(advisories, "com.example.tool", Repo, BadCommit));
        Assert.Null(AdvisoryService.Match(advisories, "com.example.tool", Repo, GoodCommit));
        Assert.NotNull(AdvisoryService.Match(advisories, "com.example.tool", Repo, null));
        Assert.Null(AdvisoryService.Match(advisories, "com.example.tool", Repo, null, unknownCommitMatches: false));
    }

    [Fact]
    public void Malware_wins_over_a_warning_and_a_missing_severity_means_malware()
    {
        var advisories = new[] { Advisory(severity: "warning"), Advisory(severity: "malware") };

        var match = AdvisoryService.Match(advisories, "com.example.tool", Repo, null)!;

        Assert.True(match.Blocks);
        Assert.Equal(AdvisorySeverity.Warning, Advisory(severity: " Warning ").Level);
        Assert.True(Advisory(severity: null).Blocks);
        Assert.True(Advisory(severity: "something new").Blocks);
    }

    [Theory]
    [InlineData(null, AdvisoryService.DefaultAdvisoriesUrl)]
    [InlineData("https://packages.example.com/registry/catalog.json?v=2", "https://packages.example.com/registry/advisories.json")]
    [InlineData("https://packages.example.com/catalog", AdvisoryService.DefaultAdvisoriesUrl)]
    [InlineData("not a url", AdvisoryService.DefaultAdvisoriesUrl)]
    public void Advisories_live_next_to_the_catalog(string? catalogUrl, string expected)
        => Assert.Equal(expected, AdvisoryService.AdvisoriesUrlFor(catalogUrl));

    [Fact]
    public async Task Advisories_are_remembered_for_when_the_registry_is_unreachable()
    {
        using var t = new TempDir("advisories");
        var cache = t.Combine("advisories.json");
        var json = """[ { "id": "A-1", "package": "com.example.tool", "title": "Pulled" }, { "title": "no package, ignored" }, null ]""";

        var online = await new AdvisoryService(StubHttpMessageHandler.Always(json), cache).LoadAsync(null);
        var offline = await new AdvisoryService(StubHttpMessageHandler.AlwaysThrows(), cache).LoadAsync(null);
        var otherRegistry = await new AdvisoryService(StubHttpMessageHandler.AlwaysThrows(), cache).LoadAsync("https://elsewhere.example.com/catalog.json");

        Assert.Equal("com.example.tool", Assert.Single(online).Package);
        Assert.Equal("Pulled", Assert.Single(offline).Title);
        Assert.Empty(otherRegistry);
    }

    [Fact]
    public async Task A_missing_advisories_file_means_none_and_clears_what_was_remembered()
    {
        using var t = new TempDir("advisories");
        var cache = t.Combine("advisories.json");

        Assert.Empty(await new AdvisoryService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound), cache).LoadAsync(null));
        Assert.False(File.Exists(cache));

        await new AdvisoryService(StubHttpMessageHandler.Always("""[ { "package": "com.example.tool", "title": "Pulled" } ]"""), cache).LoadAsync(null);
        Assert.Empty(await new AdvisoryService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound), cache).LoadAsync(null));
        Assert.Empty(await new AdvisoryService(StubHttpMessageHandler.AlwaysThrows(), cache).LoadAsync(null));
    }

    [Fact]
    public async Task Another_registry_with_no_advisories_never_overwrites_the_remembered_list()
    {
        using var t = new TempDir("advisories");
        var cache = t.Combine("advisories.json");
        await new AdvisoryService(StubHttpMessageHandler.Always("""[ { "package": "com.example.tool", "title": "Pulled" } ]"""), cache).LoadAsync(null);

        await new AdvisoryService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound), cache).LoadAsync("http://127.0.0.1:5/catalog.json");

        Assert.Single(await new AdvisoryService(StubHttpMessageHandler.AlwaysThrows(), cache).LoadAsync(null));
    }

    [Fact]
    public void The_installed_commit_comes_from_the_clone_then_the_records_then_the_lock_file()
    {
        using var t = new TempDir("installed");
        var project = t.CreateDir("Proj");
        t.WriteFile("Proj/Packages/packages-lock.json", $$"""{ "dependencies": { "com.git": { "version": "{{Repo}}#v1", "source": "git", "hash": "{{BadCommit}}" } } }""");
        Assert.Equal((Repo, BadCommit), AdvisoryService.InstalledSource(project, "com.git", Repo + "#v1", null));

        BasisPackagesStore.Set(project, "com.listed", new BasisPackageEntry { Url = Repo, Ref = "v1", Commit = GoodCommit });
        Assert.Equal((Repo, GoodCommit), AdvisoryService.InstalledSource(project, "com.listed", "file:../.basisdev/com.listed", null));

        var clone = t.CreateDir("Proj/.basisdev/com.listed");
        t.WriteFile("Proj/.basisdev/com.listed/.git/HEAD", "ref: refs/heads/main\n");
        t.WriteFile("Proj/.basisdev/com.listed/.git/packed-refs", $"# pack-refs with: peeled fully-peeled sorted\n{BadCommit} refs/heads/main\n");
        Assert.Equal(BadCommit, AdvisoryService.InstalledSource(project, "com.listed", null, clone).Commit);

        t.WriteFile("Proj/.basisdev/com.listed/.git/refs/heads/main", GoodCommit + "\n");
        Assert.Equal(GoodCommit, AdvisoryService.HeadCommit(clone));
        t.WriteFile("Proj/.basisdev/com.listed/.git/HEAD", BadCommit + "\n");
        Assert.Equal(BadCommit, AdvisoryService.HeadCommit(clone));
        t.WriteFile("Proj/.basisdev/com.listed/.git/HEAD", "ref: refs/../../escape\n");
        Assert.Null(AdvisoryService.HeadCommit(clone));
    }
}
