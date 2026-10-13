using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BasisPM.Core.Services;

/// <summary>Authenticated GitHub REST calls the publish wizard needs (whoami, look up + create a repo).</summary>
public sealed class GitHubApiService
{
    private static readonly Regex RepoPartPattern = new(@"^[A-Za-z0-9_.-]+$", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public GitHubApiService(HttpClient? http = null)
    {
        _http = http ?? HttpRetryHandler.CreateClient(TimeSpan.FromSeconds(30));
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

    public async Task<bool?> RepoExistsAsync(string owner, string name, string? token = null, CancellationToken ct = default)
    {
        if (!IsRepoPart(owner) || !IsRepoPart(name)) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{name}");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (res.IsSuccessStatusCode) return true;
            return res.StatusCode == HttpStatusCode.NotFound ? false : null;
        }
        catch (Exception ex) { DiagnosticLog.Write($"Checking whether GitHub repository {owner}/{name} exists", ex); return null; }
    }

    private static bool IsRepoPart(string? part) =>
        !string.IsNullOrEmpty(part) && RepoPartPattern.IsMatch(part) && part.Trim('.').Length > 0;

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

    public async Task<GitHubPullRequest?> FindOpenPullRequestAsync(string token, string owner, string repo, string head, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"repos/{owner}/{repo}/pulls?state=open&head={Uri.EscapeDataString(head)}", token);
        try
        {
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var pulls = await res.Content.ReadFromJsonAsync<List<GitHubPullRequest>>(cancellationToken: ct).ConfigureAwait(false);
            return pulls?.FirstOrDefault(p => p.HtmlUrl.Length > 0);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiagnosticLog.Write($"Looking for an open pull request from {head} on {owner}/{repo}", ex);
            return null;
        }
    }

    public async Task<bool> SyncForkAsync(string token, string owner, string repo, string branch, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"repos/{owner}/{repo}/merge-upstream", token);
        req.Content = JsonContent.Create(new { branch });
        try
        {
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiagnosticLog.Write($"Syncing {branch} of {owner}/{repo} with its upstream", ex);
            return false;
        }
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
        catch (Exception ex) { DiagnosticLog.Write("Loading GitHub releases", ex); return Array.Empty<GitHubRelease>(); }
    }

    public async Task<DateTimeOffset?> GetCommitDateAsync(string owner, string repo, string sha, string? token = null, CancellationToken ct = default)
    {
        if (!IsRepoPart(owner) || !IsRepoPart(repo) || sha.Length is < 7 or > 64 || !sha.All(Uri.IsHexDigit)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{owner}/{repo}/commits/{sha}");
        if (!string.IsNullOrEmpty(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var commit = await res.Content.ReadFromJsonAsync<GitHubCommit>(cancellationToken: ct).ConfigureAwait(false);
            return commit?.Commit?.Committer?.Date;
        }
        catch (Exception ex) { DiagnosticLog.Write($"Loading GitHub commit {owner}/{repo}@{sha}", ex); return null; }
    }

    public async Task<GitHubRelease> CreateReleaseAsync(string token, string owner, string repo, string tag, string name, string? body, bool prerelease, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"repos/{owner}/{repo}/releases", token);
        req.Content = JsonContent.Create(new { tag_name = tag, name, body, draft = false, prerelease });
        var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"GitHub couldn't create the release ({(int)res.StatusCode}): {text}");
        }
        return await res.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Create-release returned no body.");
    }

    public async Task<string?> GetBranchShaAsync(string token, string owner, string repo, string branch, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"repos/{owner}/{repo}/git/ref/heads/{Uri.EscapeDataString(branch)}", token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        var reference = await res.Content.ReadFromJsonAsync<GitHubRef>(cancellationToken: ct).ConfigureAwait(false);
        return reference?.Object?.Sha is { Length: > 0 } sha ? sha : null;
    }

    public async Task<bool> CreateBranchAsync(string token, string owner, string repo, string branch, string sha, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Post, $"repos/{owner}/{repo}/git/refs", token);
        req.Content = JsonContent.Create(new { @ref = "refs/heads/" + branch, sha });
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode) return true;
        if (res.StatusCode == HttpStatusCode.UnprocessableEntity) return false;
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException($"GitHub couldn't create branch {branch} on {owner}/{repo} ({(int)res.StatusCode}): {text}");
    }

    public async Task<string?> GetFileShaAsync(string token, string owner, string repo, string path, string branch, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Get, $"repos/{owner}/{repo}/contents/{EscapePath(path)}?ref={Uri.EscapeDataString(branch)}", token);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        var file = await res.Content.ReadFromJsonAsync<GitHubContent>(cancellationToken: ct).ConfigureAwait(false);
        return file?.Sha is { Length: > 0 } sha ? sha : null;
    }

    public async Task PutFileAsync(string token, string owner, string repo, string path, string branch, string message, string content, string? sha, CancellationToken ct = default)
    {
        using var req = Request(HttpMethod.Put, $"repos/{owner}/{repo}/contents/{EscapePath(path)}", token);
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        req.Content = sha is null
            ? JsonContent.Create(new { message, content = encoded, branch })
            : JsonContent.Create(new { message, content = encoded, branch, sha });
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new InvalidOperationException($"GitHub couldn't write {path} on {owner}/{repo} ({(int)res.StatusCode}): {text}");
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}

public sealed class GitHubRef
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "";
    [JsonPropertyName("object")] public GitHubRefObject? Object { get; set; }
}

public sealed class GitHubRefObject
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
}

public sealed class GitHubContent
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
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
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
}

public sealed class GitHubCommit
{
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    [JsonPropertyName("commit")] public GitHubCommitDetail? Commit { get; set; }
}

public sealed class GitHubCommitDetail
{
    [JsonPropertyName("committer")] public GitHubCommitPerson? Committer { get; set; }
}

public sealed class GitHubCommitPerson
{
    [JsonPropertyName("date")] public DateTimeOffset? Date { get; set; }
}
