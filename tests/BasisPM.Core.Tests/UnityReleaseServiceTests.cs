using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityReleaseServiceTests
{
    private static string Page(int total, params string[] versions)
    {
        var results = string.Join(",", versions.Select(v =>
            $$"""{ "version": "{{v}}", "stream": "LTS", "recommended": false }"""));
        return $$"""{ "offset": 0, "limit": 25, "total": {{total}}, "results": [ {{results}} ] }""";
    }

    [Fact]
    public async Task FetchAll_accumulates_across_pages()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("offset=0")) return StubHttpMessageHandler.Json(HttpStatusCode.OK, Page(3, "6000.0.10f1", "6000.0.20f1"));
            if (url.Contains("offset=2")) return StubHttpMessageHandler.Json(HttpStatusCode.OK, Page(3, "6000.0.30f1"));
            return StubHttpMessageHandler.Json(HttpStatusCode.OK, Page(3));
        });
        var svc = new UnityReleaseService(new HttpClient(handler));

        var releases = await svc.FetchAllAsync();

        Assert.Equal(3, releases.Count);
    }

    [Fact]
    public async Task FetchAll_sorts_parseable_newest_first_and_unparseable_last()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Page(3, "6000.0.10f1", "garbage", "6000.0.30f1")));
        var svc = new UnityReleaseService(new HttpClient(handler));

        var releases = await svc.FetchAllAsync();

        Assert.Equal(new[] { "6000.0.30f1", "6000.0.10f1", "garbage" },
            releases.Select(r => r.Version).ToArray());
    }

    [Fact]
    public async Task FetchAll_returns_empty_when_first_page_is_empty()
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(HttpStatusCode.OK, Page(0)));
        var svc = new UnityReleaseService(new HttpClient(handler));

        Assert.Empty(await svc.FetchAllAsync());
    }

    [Fact]
    public void Display_includes_recommended_marker()
    {
        var recommended = new UnityRelease { Version = "6000.0.25f1", Stream = "LTS", Recommended = true };
        var normal = new UnityRelease { Version = "6000.0.25f1", Stream = "LTS", Recommended = false };

        Assert.Contains("recommended", recommended.Display);
        Assert.DoesNotContain("recommended", normal.Display);
        Assert.Contains("6000.0.25f1", normal.Display);
    }
}
