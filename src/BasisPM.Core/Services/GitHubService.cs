using System.Net.Http.Json;
using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class GitHubService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;

    public GitHubService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BasisManifest/1.0");
    }

    public static GitHubLocator Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException("Empty GitHub reference.");

        var raw = input.Trim();
        string? branch = null;
        string? path = null;

        var hashIdx = raw.IndexOf('#');
        if (hashIdx >= 0)
        {
            branch = raw[(hashIdx + 1)..].Trim();
            raw = raw[..hashIdx];
        }

        var queryIdx = raw.IndexOf('?');
        if (queryIdx >= 0)
        {
            var query = raw[(queryIdx + 1)..];
            raw = raw[..queryIdx];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                    path = Uri.UnescapeDataString(kv[1]);
            }
        }

        var stripped = raw
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("git@", "", StringComparison.OrdinalIgnoreCase)
            .Replace("github.com:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("github.com/", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        if (stripped.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            stripped = stripped[..^4];

        var segments = stripped.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new FormatException("Expected owner/repo or full GitHub URL.");

        var owner = segments[0];
        var repo = segments[1];

        if (segments.Length > 3 &&
            (segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase) ||
             segments[2].Equals("blob", StringComparison.OrdinalIgnoreCase)))
        {
            branch ??= segments[3];
            if (segments.Length > 4)
                path ??= string.Join('/', segments.Skip(4));
        }

        return new GitHubLocator(owner, repo, branch, string.IsNullOrEmpty(path) ? null : path.Trim('/'));
    }

    public async Task<UpmPackageJson?> FetchPackageJsonAsync(GitHubLocator loc, CancellationToken ct = default)
    {
        var branch = string.IsNullOrEmpty(loc.Branch) ? "HEAD" : loc.Branch;
        var pathPart = string.IsNullOrEmpty(loc.Path) ? "" : $"{loc.Path}/";
        var url = $"https://raw.githubusercontent.com/{loc.Owner}/{loc.Repo}/{branch}/{pathPart}package.json";

        try
        {
            return await _http.GetFromJsonAsync<UpmPackageJson>(url, JsonOpts, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public static string BuildManifestUrl(GitHubLocator loc)
    {
        var url = $"https://github.com/{loc.Owner}/{loc.Repo}.git";
        if (!string.IsNullOrEmpty(loc.Path))
            url += $"?path={loc.Path}";
        if (!string.IsNullOrEmpty(loc.Branch))
            url += $"#{loc.Branch}";
        return url;
    }
}
