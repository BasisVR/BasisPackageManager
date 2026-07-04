using System.Net;
using System.Net.Http.Json;
using BasisPM.Server.Tests.Infrastructure;
using Xunit;

namespace BasisPM.Server.Tests;

internal static class Submissions
{
    public static object Valid(string id, string gitUrl = "https://github.com/someone/repo.git")
        => new { id, name = "Test Package", gitUrl };

    public static string UniqueId() => "com.test." + Guid.NewGuid().ToString("N");
}

public sealed class SubmissionDisabledTests : IClassFixture<DisabledSubmissionsFactory>
{
    private readonly HttpClient _client;
    public SubmissionDisabledTests(DisabledSubmissionsFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Post_is_rejected_when_submissions_are_disabled()
    {
        var res = await _client.PostAsJsonAsync("/api/packages", Submissions.Valid(Submissions.UniqueId()));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }
}

public sealed class SubmissionOpenTests : IClassFixture<OpenSubmissionsFactory>
{
    private readonly HttpClient _client;
    public SubmissionOpenTests(OpenSubmissionsFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Valid_submission_is_created_and_then_retrievable()
    {
        var id = Submissions.UniqueId();
        var res = await _client.PostAsJsonAsync("/api/packages", Submissions.Valid(id));

        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        Assert.Contains(id, res.Headers.Location?.ToString());

        var fetched = await _client.GetAsync($"/api/packages/{id}");
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task Invalid_git_url_is_a_bad_request()
    {
        var res = await _client.PostAsJsonAsync("/api/packages",
            Submissions.Valid(Submissions.UniqueId(), gitUrl: "https://evil.example.com/x.git"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Duplicate_id_is_a_conflict()
    {
        var id = Submissions.UniqueId();
        Assert.Equal(HttpStatusCode.Created, (await _client.PostAsJsonAsync("/api/packages", Submissions.Valid(id))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsJsonAsync("/api/packages", Submissions.Valid(id))).StatusCode);
    }

    [Fact]
    public async Task Cannot_repoint_a_seed_package()
    {
        var res = await _client.PostAsJsonAsync("/api/packages",
            Submissions.Valid("com.basis.pooltable", gitUrl: "https://github.com/attacker/evil.git"));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }
}

public sealed class SubmissionTokenTests : IClassFixture<TokenSubmissionsFactory>
{
    private readonly HttpClient _client;
    public SubmissionTokenTests(TokenSubmissionsFactory factory) => _client = factory.CreateClient();

    private async Task<HttpResponseMessage> PostAsync(object body, string? token)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/packages")
        {
            Content = JsonContent.Create(body),
        };
        if (token is not null) req.Headers.Add("X-Submit-Token", token);
        return await _client.SendAsync(req);
    }

    [Fact]
    public async Task Missing_token_is_unauthorized()
    {
        var res = await PostAsync(Submissions.Valid(Submissions.UniqueId()), token: null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Wrong_token_is_unauthorized()
    {
        var res = await PostAsync(Submissions.Valid(Submissions.UniqueId()), token: "not-the-token");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Correct_token_creates_the_package()
    {
        var res = await PostAsync(Submissions.Valid(Submissions.UniqueId()), token: TokenSubmissionsFactory.Token);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }
}
