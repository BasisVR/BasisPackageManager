using System.Net;
using System.Text;
using BasisPM.Core.Models;
using BasisPM.Server.Services;
using Xunit;

namespace BasisPM.Server.Tests;

public sealed class PackageVersionSourceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static UpmGitUrl Repo(string url) => UpmGitUrl.Parse(url)!;

    [Fact]
    public async Task GitHub_tags_follow_the_next_page_link_and_keep_commits()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("page=2")
            ? Json("""[ { "name": "v1.0.0", "commit": { "sha": "aaa" } } ]""")
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[ { "name": "v1.1.0", "commit": { "sha": "bbb" } } ]""", Encoding.UTF8, "application/json"),
                Headers = { { "Link", "<https://api.github.com/repositories/1/tags?per_page=100&page=2>; rel=\"next\"" } },
            });
        var source = new HttpPackageVersionSource("token", new HttpClient(handler));

        var tags = await source.ListTagsAsync(Repo("https://github.com/someone/thing.git"));

        Assert.Equal(new[] { "v1.1.0", "v1.0.0" }, tags!.Select(t => t.Name).ToArray());
        Assert.Equal(new[] { "bbb", "aaa" }, tags!.Select(t => t.Commit).ToArray());
        Assert.Equal("https://api.github.com/repos/someone/thing/tags?per_page=100", handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task GitLab_tags_use_the_project_path_and_commit_id()
    {
        var handler = new StubHandler(_ => Json("""[ { "name": "v1.2.0", "commit": { "id": "ccc" } } ]"""));
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));

        var tags = await source.ListTagsAsync(Repo("https://gitlab.com/s-ilent/SCSS.git"));

        Assert.Equal("ccc", tags!.Single().Commit);
        Assert.Equal("https://gitlab.com/api/v4/projects/s-ilent%2FSCSS/repository/tags?per_page=100", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_rate_limited_api_without_git_reports_no_tags()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } });
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));

        Assert.Null(await source.ListTagsAsync(Repo("https://github.com/someone/thing.git")));
        Assert.Null(await source.RepoExistsAsync(Repo("https://github.com/someone/other.git")));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Package_json_is_read_from_the_tag_and_sub_folder()
    {
        var handler = new StubHandler(_ => Json("""{ "name": "com.someone.thing", "version": "1.0.0", "unity": "6000.0" }"""));
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));

        var lookup = await source.ReadPackageJsonAsync(Repo("https://github.com/someone/thing.git?path=/Packages/com.someone.thing"), "v1.0.0", isTag: true);

        Assert.True(lookup.Reachable);
        Assert.Equal("6000.0", lookup.Package!.Unity);
        Assert.Equal("https://raw.githubusercontent.com/someone/thing/refs/tags/v1.0.0/Packages/com.someone.thing/package.json", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GitLab_package_json_uses_the_files_api_with_the_ref()
    {
        var handler = new StubHandler(_ => Json("""{ "name": "com.s-ilent.crosstone" }"""));
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));

        await source.ReadPackageJsonAsync(Repo("https://gitlab.com/s-ilent/SCSS.git"), "v1.22", isTag: true);

        Assert.Equal("https://gitlab.com/api/v4/projects/s-ilent%2FSCSS/repository/files/package.json/raw?ref=v1.22", handler.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Missing_and_unreachable_package_json_are_told_apart()
    {
        var missing = new HttpPackageVersionSource(null, new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        var failing = new HttpPackageVersionSource(null, new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway))));

        var gone = await missing.ReadPackageJsonAsync(Repo("https://github.com/someone/thing.git"), null, isTag: false);
        var down = await failing.ReadPackageJsonAsync(Repo("https://github.com/someone/thing.git"), null, isTag: false);

        Assert.True(gone.Reachable);
        Assert.Null(gone.Package);
        Assert.False(down.Reachable);
    }

    [Fact]
    public async Task Repeated_reads_are_cached_for_the_run()
    {
        var handler = new StubHandler(_ => Json("""{ "name": "com.someone.thing" }"""));
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));
        var repo = Repo("https://github.com/someone/thing.git");

        await source.ReadPackageJsonAsync(repo, "v1.0.0", isTag: true);
        await source.ReadPackageJsonAsync(repo, "v1.0.0", isTag: true);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Repo_exists_maps_found_and_not_found()
    {
        var handler = new StubHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/gone") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Json("{}"));
        var source = new HttpPackageVersionSource(null, new HttpClient(handler));

        Assert.True(await source.RepoExistsAsync(Repo("https://github.com/someone/thing.git")));
        Assert.False(await source.RepoExistsAsync(Repo("https://github.com/someone/gone.git")));
    }
}
