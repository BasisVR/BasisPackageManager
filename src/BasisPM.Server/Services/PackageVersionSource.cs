using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Server.Services;

public sealed record RepoTag(string Name, string? Commit);

public sealed record PackageJsonLookup(bool Reachable, UpmPackageJson? Package)
{
    public static readonly PackageJsonLookup Unreachable = new(false, null);
    public static readonly PackageJsonLookup Missing = new(true, null);
}

public interface IPackageVersionSource
{
    Task<IReadOnlyList<RepoTag>?> ListTagsAsync(UpmGitUrl repo, CancellationToken ct = default);
    Task<PackageJsonLookup> ReadPackageJsonAsync(UpmGitUrl repo, string? gitRef, bool isTag, CancellationToken ct = default);
    Task<bool?> RepoExistsAsync(UpmGitUrl repo, CancellationToken ct = default);
}

public sealed class HttpPackageVersionSource : IPackageVersionSource
{
    private const int MaxTagPages = 5;

    private readonly HttpClient _http;
    private readonly string? _githubToken;
    private readonly GitService? _git;
    private readonly ConcurrentDictionary<string, Task<IReadOnlyList<RepoTag>?>> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<PackageJsonLookup>> _manifests = new(StringComparer.Ordinal);
    private volatile bool _githubApiLimited;

    public HttpPackageVersionSource(string? githubToken = null, HttpClient? http = null, GitService? git = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any()) _http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisPackageManager/1.0");
        _githubToken = string.IsNullOrWhiteSpace(githubToken) ? null : githubToken.Trim();
        _git = git;
    }

    public Task<IReadOnlyList<RepoTag>?> ListTagsAsync(UpmGitUrl repo, CancellationToken ct = default) =>
        _tags.GetOrAdd(repo.CloneUrl, _ => FetchTagsAsync(repo, ct));

    public Task<PackageJsonLookup> ReadPackageJsonAsync(UpmGitUrl repo, string? gitRef, bool isTag, CancellationToken ct = default) =>
        _manifests.GetOrAdd($"{repo.CloneUrl}|{repo.Path}|{(isTag ? "tag" : "ref")}|{gitRef}", _ => FetchPackageJsonAsync(repo, gitRef, isTag, ct));

    public async Task<bool?> RepoExistsAsync(UpmGitUrl repo, CancellationToken ct = default)
    {
        string? url = repo.IsGitHub && !_githubApiLimited ? $"https://api.github.com/repos/{Segment(repo.Owner)}/{Segment(repo.Repo)}"
            : repo.IsGitLab ? $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repo.Owner + "/" + repo.Repo)}"
            : null;
        if (url is not null)
        {
            try
            {
                using var response = await SendAsync(url, repo.IsGitHub, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
                if (response.StatusCode == HttpStatusCode.NotFound) return false;
                NoteRateLimit(response, repo.IsGitHub);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { DiagnosticLog.Write($"Checking that {repo.CloneUrl} exists", ex); }
        }
        if (_git is null) return null;
        var heads = await _git.ListRemoteHeadsAsync(repo.CloneUrl, ct).ConfigureAwait(false);
        return heads.Count > 0 ? true : null;
    }

    private async Task<IReadOnlyList<RepoTag>?> FetchTagsAsync(UpmGitUrl repo, CancellationToken ct)
    {
        if (repo.IsGitHub && !_githubApiLimited)
        {
            var tags = await PagedTagsAsync($"https://api.github.com/repos/{Segment(repo.Owner)}/{Segment(repo.Repo)}/tags?per_page=100", true, "sha", ct).ConfigureAwait(false);
            if (tags is not null) return tags;
        }
        else if (repo.IsGitLab)
        {
            var tags = await PagedTagsAsync($"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repo.Owner + "/" + repo.Repo)}/repository/tags?per_page=100", false, "id", ct).ConfigureAwait(false);
            if (tags is not null) return tags;
        }
        if (_git is null) return null;
        var names = await _git.ListRemoteTagsAsync(repo.CloneUrl, ct).ConfigureAwait(false);
        return names.Count == 0 ? null : names.Select(name => new RepoTag(name, null)).ToList();
    }

    private async Task<IReadOnlyList<RepoTag>?> PagedTagsAsync(string firstPage, bool github, string commitField, CancellationToken ct)
    {
        var tags = new List<RepoTag>();
        var url = firstPage;
        for (var page = 0; page < MaxTagPages && url is not null; page++)
        {
            try
            {
                using var response = await SendAsync(url, github, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    NoteRateLimit(response, github);
                    return page == 0 ? null : tags;
                }
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (document.RootElement.ValueKind != JsonValueKind.Array) return page == 0 ? null : tags;
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) continue;
                    string? commit = null;
                    if (item.TryGetProperty("commit", out var target) && target.ValueKind == JsonValueKind.Object && target.TryGetProperty(commitField, out var sha) && sha.ValueKind == JsonValueKind.String)
                        commit = sha.GetString();
                    if (name.GetString() is { Length: > 0 } tag) tags.Add(new RepoTag(tag, commit));
                }
                url = NextPage(response);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                DiagnosticLog.Write($"Listing the tags at {url}", ex);
                return page == 0 ? null : tags;
            }
        }
        return tags;
    }

    private async Task<PackageJsonLookup> FetchPackageJsonAsync(UpmGitUrl repo, string? gitRef, bool isTag, CancellationToken ct)
    {
        var file = string.IsNullOrEmpty(repo.Path) ? PackageJsonFile.FileName : repo.Path.Trim('/') + "/" + PackageJsonFile.FileName;
        string url;
        if (repo.IsGitHub)
        {
            var reference = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : (isTag ? "refs/tags/" : "") + gitRef.Trim();
            url = $"https://raw.githubusercontent.com/{Segment(repo.Owner)}/{Segment(repo.Repo)}/{Segments(reference)}/{Segments(file)}";
        }
        else if (repo.IsGitLab)
            url = $"https://gitlab.com/api/v4/projects/{Uri.EscapeDataString(repo.Owner + "/" + repo.Repo)}/repository/files/{Uri.EscapeDataString(file)}/raw?ref={Uri.EscapeDataString(string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim())}";
        else return PackageJsonLookup.Unreachable;
        try
        {
            using var response = await SendAsync(url, false, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return PackageJsonLookup.Missing;
            if (!response.IsSuccessStatusCode) return PackageJsonLookup.Unreachable;
            var package = PackageJsonFile.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            return package is null ? PackageJsonLookup.Missing : new PackageJsonLookup(true, package);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            DiagnosticLog.Write($"Reading {url}", ex);
            return PackageJsonLookup.Unreachable;
        }
    }

    private Task<HttpResponseMessage> SendAsync(string url, bool githubApi, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (githubApi)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (_githubToken is not null) request.Headers.Authorization = new("Bearer", _githubToken);
        }
        return _http.SendAsync(request, ct);
    }

    private void NoteRateLimit(HttpResponseMessage response, bool github)
    {
        if (!github) return;
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0"))
            _githubApiLimited = true;
    }

    private static string? NextPage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var links)) return null;
        foreach (var part in string.Join(",", links).Split(','))
        {
            var pieces = part.Split(';');
            if (pieces.Length < 2 || !pieces.Skip(1).Any(p => p.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase))) continue;
            var target = pieces[0].Trim().TrimStart('<').TrimEnd('>');
            return Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        }
        return null;
    }

    private static string Segment(string value) => Uri.EscapeDataString(value);

    private static string Segments(string path) => string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
