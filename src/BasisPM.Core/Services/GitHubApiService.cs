using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Services;

/// <summary>Authenticated GitHub REST calls the publish wizard needs (whoami, look up + create a repo).</summary>
public sealed class GitHubApiService
{
    private readonly HttpClient _http;

    public GitHubApiService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.BaseAddress ??= new Uri("https://api.github.com/");
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisPackageManager/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    private HttpRequestMessage Request(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public async Task<GitHubUser?> GetUserAsync(string token, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, "user", token);
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: ct).ConfigureAwait(false) : null;
    }

    /// <summary>The repo, or null if it doesn't exist / isn't visible to this token.</summary>
    public async Task<GitHubRepo?> GetRepoAsync(string token, string owner, string name, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"repos/{owner}/{name}", token);
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        return res.IsSuccessStatusCode ? await res.Content.ReadFromJsonAsync<GitHubRepo>(cancellationToken: ct).ConfigureAwait(false) : null;
    }

    /// <summary>Creates an empty repo under the authenticated user's account (no auto-init — we push our own tree).</summary>
    public async Task<GitHubRepo> CreateRepoAsync(string token, string name, string? description, bool isPrivate, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, "user/repos", token);
        req.Content = JsonContent.Create(new { name, description, @private = isPrivate, auto_init = false });
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var hint = res.StatusCode == HttpStatusCode.UnprocessableEntity ? " (does a repo with that name already exist?)" : "";
            throw new InvalidOperationException($"GitHub couldn't create the repository ({(int)res.StatusCode}){hint}: {body}");
        }
        return await res.Content.ReadFromJsonAsync<GitHubRepo>(cancellationToken: ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("GitHub create-repo returned no body.");
    }

    /// <summary>Forks a repo into the authenticated user's account (GitHub returns the existing fork if there is one).</summary>
    public async Task<GitHubRepo> ForkRepoAsync(string token, string owner, string repo, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"repos/{owner}/{repo}/forks", token);
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"GitHub couldn't fork {owner}/{repo} ({(int)res.StatusCode}): {body}");
        }
        return await res.Content.ReadFromJsonAsync<GitHubRepo>(cancellationToken: ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Fork returned no body.");
    }

    /// <summary>Opens a PR on <paramref name="owner"/>/<paramref name="repo"/>. <paramref name="head"/> is "branch" (same repo) or "forkOwner:branch".</summary>
    public async Task<GitHubPullRequest> CreatePullRequestAsync(string token, string owner, string repo, string title, string head, string baseBranch, string? body, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"repos/{owner}/{repo}/pulls", token);
        req.Content = JsonContent.Create(new { title, head, @base = baseBranch, body, maintainer_can_modify = true });
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var b = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"GitHub couldn't open the pull request ({(int)res.StatusCode}): {b}");
        }
        return await res.Content.ReadFromJsonAsync<GitHubPullRequest>(cancellationToken: ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Create-PR returned no body.");
    }

    /// <summary>A repo's releases (newest first per GitHub). Token optional — public repos work unauthenticated
    /// (just a lower rate limit). Returns empty on any error or if there are no releases.</summary>
    public async Task<IReadOnlyList<GitHubRelease>> GetReleasesAsync(string owner, string repo, string? token = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/releases?per_page=100");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<GitHubRelease>();
            return await res.Content.ReadFromJsonAsync<List<GitHubRelease>>(cancellationToken: ct).ConfigureAwait(false)
                   ?? (IReadOnlyList<GitHubRelease>)Array.Empty<GitHubRelease>();
        }
        catch { return Array.Empty<GitHubRelease>(); }
    }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")] public string Login { get; set; } = "";
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }

    /// <summary>The GitHub no-reply commit email, so a commit works without exposing a real address.</summary>
    public string NoReplyEmail => $"{Id}+{Login}@users.noreply.github.com";
}

public sealed class GitHubRepo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("clone_url")] public string CloneUrl { get; set; } = "";
    [JsonPropertyName("default_branch")] public string? DefaultBranch { get; set; }
    [JsonPropertyName("private")] public bool Private { get; set; }
    [JsonPropertyName("fork")] public bool Fork { get; set; }
    [JsonPropertyName("owner")] public GitHubUser? Owner { get; set; }
    [JsonPropertyName("permissions")] public GitHubPermissions? Permissions { get; set; }

    /// <summary>Whether the token can push here directly (else the PR flow forks first).</summary>
    public bool CanPush => Permissions?.Push == true;
}

public sealed class GitHubPermissions
{
    [JsonPropertyName("push")] public bool Push { get; set; }
    [JsonPropertyName("maintain")] public bool Maintain { get; set; }
    [JsonPropertyName("admin")] public bool Admin { get; set; }
}

public sealed class GitHubPullRequest
{
    [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
}

public sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
    [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
}
