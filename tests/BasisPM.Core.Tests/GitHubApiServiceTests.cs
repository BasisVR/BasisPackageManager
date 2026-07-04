using System.Net;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitHubApiServiceTests
{
    private static (GitHubApiService api, StubHttpMessageHandler handler) Api(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubApiService(client), handler);
    }

    private static HttpResponseMessage Ok(string json) => StubHttpMessageHandler.Json(HttpStatusCode.OK, json);

    [Fact]
    public async Task GetUser_returns_user_and_sends_bearer_token()
    {
        var (api, handler) = Api(_ => Ok("""{ "login": "octocat", "id": 583231, "name": "The Octocat" }"""));

        var user = await api.GetUserAsync("tok123");

        Assert.NotNull(user);
        Assert.Equal("octocat", user!.Login);
        Assert.Equal("583231+octocat@users.noreply.github.com", user.NoReplyEmail);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("tok123", handler.Requests[0].Headers.Authorization?.Parameter);
        Assert.EndsWith("/user", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GetUser_returns_null_on_unauthorized()
    {
        var (api, _) = Api(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        Assert.Null(await api.GetUserAsync("bad"));
    }

    [Fact]
    public async Task GetRepo_maps_permissions_to_can_push()
    {
        var (api, _) = Api(_ => Ok("""
            { "name": "repo", "full_name": "o/repo", "default_branch": "main",
              "permissions": { "push": true } }
        """));

        var repo = await api.GetRepoAsync("tok", "o", "repo");

        Assert.NotNull(repo);
        Assert.Equal("main", repo!.DefaultBranch);
        Assert.True(repo.CanPush);
    }

    [Fact]
    public async Task GetRepo_returns_null_on_not_found()
    {
        var (api, _) = Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await api.GetRepoAsync("tok", "o", "missing"));
    }

    [Fact]
    public async Task CreateRepo_posts_and_returns_repo()
    {
        var (api, handler) = Api(_ => StubHttpMessageHandler.Json(HttpStatusCode.Created,
            """{ "name": "new-repo", "full_name": "me/new-repo", "clone_url": "https://github.com/me/new-repo.git" }"""));

        var repo = await api.CreateRepoAsync("tok", "new-repo", "desc", isPrivate: false);

        Assert.Equal("new-repo", repo.Name);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.EndsWith("/user/repos", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CreateRepo_throws_with_hint_on_422()
    {
        var (api, _) = Api(_ => StubHttpMessageHandler.Json(HttpStatusCode.UnprocessableEntity, """{ "message": "exists" }"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.CreateRepoAsync("tok", "dup", null, false));
        Assert.Contains("already exist", ex.Message);
    }

    [Fact]
    public async Task ForkRepo_returns_the_fork()
    {
        var (api, handler) = Api(_ => Ok("""{ "name": "repo", "full_name": "me/repo", "owner": { "login": "me", "id": 1 } }"""));

        var fork = await api.ForkRepoAsync("tok", "upstream", "repo");

        Assert.Equal("me", fork.Owner?.Login);
        Assert.EndsWith("/repos/upstream/repo/forks", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task ForkRepo_throws_on_error()
    {
        var (api, _) = Api(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Assert.ThrowsAsync<InvalidOperationException>(() => api.ForkRepoAsync("tok", "o", "r"));
    }

    [Fact]
    public async Task CreatePullRequest_returns_html_url()
    {
        var (api, handler) = Api(_ => StubHttpMessageHandler.Json(HttpStatusCode.Created,
            """{ "html_url": "https://github.com/o/r/pull/7", "number": 7, "state": "open" }"""));

        var pr = await api.CreatePullRequestAsync("tok", "o", "r", "Title", "me:branch", "main", "body");

        Assert.Equal("https://github.com/o/r/pull/7", pr.HtmlUrl);
        Assert.Equal(7, pr.Number);
        Assert.EndsWith("/repos/o/r/pulls", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task CreatePullRequest_throws_on_error()
    {
        var (api, _) = Api(_ => StubHttpMessageHandler.Json(HttpStatusCode.UnprocessableEntity, """{ "message": "bad" }"""));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.CreatePullRequestAsync("tok", "o", "r", "t", "h", "main", null));
    }

    [Fact]
    public async Task GetReleases_returns_list_on_success()
    {
        var (api, _) = Api(_ => Ok("""
            [ { "tag_name": "v1.0.0", "prerelease": false, "draft": false },
              { "tag_name": "v1.1.0-beta", "prerelease": true, "draft": false } ]
        """));

        var releases = await api.GetReleasesAsync("o", "r");

        Assert.Equal(2, releases.Count);
        Assert.Equal("v1.0.0", releases[0].TagName);
        Assert.True(releases[1].Prerelease);
    }

    [Fact]
    public async Task GetReleases_returns_empty_on_error_status()
    {
        var (api, _) = Api(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Empty(await api.GetReleasesAsync("o", "r"));
    }

    [Fact]
    public async Task GetReleases_returns_empty_on_network_failure()
    {
        var (api, _) = Api(_ => throw new HttpRequestException("down"));
        Assert.Empty(await api.GetReleasesAsync("o", "r"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void GitHubRepo_CanPush_reflects_push_permission(bool push, bool expected)
    {
        var repo = new GitHubRepo { Permissions = new GitHubPermissions { Push = push } };
        Assert.Equal(expected, repo.CanPush);
    }

    [Fact]
    public void GitHubRepo_CanPush_is_false_without_permissions()
    {
        Assert.False(new GitHubRepo().CanPush);
    }

    [Fact]
    public void GitHubUser_NoReplyEmail_uses_id_and_login()
    {
        var user = new GitHubUser { Login = "alice", Id = 42 };
        Assert.Equal("42+alice@users.noreply.github.com", user.NoReplyEmail);
    }
}
