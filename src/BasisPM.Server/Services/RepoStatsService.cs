using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core.Services;

namespace BasisPM.Server.Services;

public sealed record RepoStats(int Stars, int Forks, string? Description, string? Updated);

// Pulls real stars / forks / last-updated from the GitHub or GitLab API so the
// registry never ships faked numbers. Results are cached per repo for the run.
public sealed class RepoStatsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string? _githubToken;
    private readonly ConcurrentDictionary<string, RepoStats?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RepoStatsService(string? githubToken = null)
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisPackageManager/1.0");
        _githubToken = string.IsNullOrWhiteSpace(githubToken) ? null : githubToken.Trim();
    }

    public async Task<RepoStats?> FetchAsync(string? repoUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return null;
        var url = repoUrl.Trim();

        var (host, owner, repo) = Parse(url);
        if (host is null || owner is null || repo is null) return null;

        var key = $"{host}/{owner}/{repo}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        RepoStats? stats = null;
        try
        {
            if (host == "github")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                if (_githubToken is not null) request.Headers.Authorization = new("Bearer", _githubToken);
                using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var r = await response.Content.ReadFromJsonAsync<GitHubRepo>(JsonOpts, ct).ConfigureAwait(false);
                if (r is not null) stats = new RepoStats(r.StargazersCount, r.ForksCount, r.Description, r.PushedAt);
            }
            else if (host == "gitlab")
            {
                var path = Uri.EscapeDataString($"{owner}/{repo}");
                var r = await _http.GetFromJsonAsync<GitLabProject>($"https://gitlab.com/api/v4/projects/{path}", JsonOpts, ct).ConfigureAwait(false);
                if (r is not null) stats = new RepoStats(r.StarCount, r.ForksCount, r.Description, r.LastActivityAt);
            }
        }
        catch (Exception ex) { DiagnosticLog.Write($"Loading repository statistics for {url}", ex); stats = null; }

        _cache[key] = stats;
        return stats;
    }

    private static (string? host, string? owner, string? repo) Parse(string url)
    {
        var host = url.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "github"
                 : url.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase) ? "gitlab"
                 : null;
        if (host is null) return (null, null, null);

        var marker = host == "github" ? "github.com" : "gitlab.com";
        var rest = url[(url.IndexOf(marker, StringComparison.OrdinalIgnoreCase) + marker.Length)..].Trim('/', ':');
        var cut = rest.IndexOfAny(new[] { '?', '#' });
        if (cut >= 0) rest = rest[..cut];
        var webPath = rest.IndexOf("/-/", StringComparison.Ordinal);
        if (webPath >= 0) rest = rest[..webPath];
        rest = rest.TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];

        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return (host, null, null);
        return host == "gitlab" ? (host, string.Join('/', parts[..^1]), parts[^1]) : (host, parts[0], parts[1]);
    }

    private sealed class GitHubRepo
    {
        [JsonPropertyName("stargazers_count")] public int StargazersCount { get; set; }
        [JsonPropertyName("forks_count")] public int ForksCount { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("pushed_at")] public string? PushedAt { get; set; }
    }

    private sealed class GitLabProject
    {
        [JsonPropertyName("star_count")] public int StarCount { get; set; }
        [JsonPropertyName("forks_count")] public int ForksCount { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("last_activity_at")] public string? LastActivityAt { get; set; }
    }
}
