using System.Text.RegularExpressions;

namespace BasisPM.Core.Models;

/// <summary>
/// A parsed Unity UPM git dependency URL — the base clone URL plus its optional <c>?path=</c> subfolder
/// and <c>#revision</c>. Handles https, scp-style ssh (<c>git@host:owner/repo</c>), and a trailing <c>.git</c>.
/// </summary>
public sealed record UpmGitUrl(string Host, string Owner, string Repo, string CloneUrl, string? Ref, string? Path)
{
    public bool IsGitHub => Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);
    public bool IsGitLab => Host.Equals("gitlab.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>owner/repo (GitHub) or group/.../repo (GitLab).</summary>
    public string Slug => IsGitLab ? $"{Owner}/{Repo}" : $"{Owner}/{Repo}";

    /// <summary>Rebuilds a UPM manifest URL: clone URL + optional <c>?path=</c> and <c>#ref</c>.
    /// Pass a ref callers have validated via <c>GitUrlPolicy.IsSafeRef</c>; a null ref = the default branch.</summary>
    public string ToManifestUrl(string? gitRef = null, string? subPath = null)
    {
        var url = CloneUrl;
        var path = subPath ?? Path;
        if (!string.IsNullOrEmpty(path)) url += $"?path={path}";
        if (!string.IsNullOrEmpty(gitRef)) url += $"#{gitRef}";
        return url;
    }

    public static UpmGitUrl? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var raw = url.Trim();
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;

        string? refName = null, path = null;
        var hash = raw.IndexOf('#');
        if (hash >= 0) { refName = raw[(hash + 1)..].Trim(); raw = raw[..hash]; }

        var q = raw.IndexOf('?');
        if (q >= 0)
        {
            var query = raw[(q + 1)..];
            raw = raw[..q];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("path", StringComparison.OrdinalIgnoreCase))
                    path = Uri.UnescapeDataString(kv[1]).Trim('/');
            }
        }

        if (raw.StartsWith("git+", StringComparison.OrdinalIgnoreCase)) raw = raw[4..];

        string host, rest;
        if (raw.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
            host = uri.Host;
            rest = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else
        {
            var m = Regex.Match(raw, @"^(?:[^@/:]+@)?([^/:]+)[:/](.+)$");
            if (!m.Success) return null;
            host = m.Groups[1].Value;
            rest = m.Groups[2].Value;
        }

        host = host.ToLowerInvariant();
        if (host is "www.github.com" or "www.gitlab.com") host = host[4..];
        rest = rest.Trim('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];

        var segs = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) return null;

        string owner, repo;
        if (host == "github.com") { owner = segs[0]; repo = segs[1]; }
        else { repo = segs[^1]; owner = string.Join('/', segs[..^1]); }

        var cloneUrl = host is "github.com" or "gitlab.com" ? $"https://{host}/{owner}/{repo}.git" : raw;
        return new UpmGitUrl(host, owner, repo, cloneUrl,
            string.IsNullOrEmpty(refName) ? null : refName,
            string.IsNullOrEmpty(path) ? null : path);
    }
}
