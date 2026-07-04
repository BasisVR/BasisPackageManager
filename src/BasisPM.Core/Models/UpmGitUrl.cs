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

    public static UpmGitUrl? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var raw = url.Trim();

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

        raw = raw.Replace("git+", "", StringComparison.OrdinalIgnoreCase);
        var m = Regex.Match(raw, @"^(?:https?://|ssh://git@|git@)?([^/:]+)[/:]+(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var host = m.Groups[1].Value.ToLowerInvariant();
        var rest = m.Groups[2].Value.TrimEnd('/');
        if (rest.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) rest = rest[..^4];

        var segs = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < 2) return null;

        string owner, repo;
        if (host == "gitlab.com") { repo = segs[^1]; owner = string.Join('/', segs[..^1]); }
        else { owner = segs[0]; repo = segs[1]; }

        var cloneUrl = $"https://{host}/{owner}/{repo}.git";
        return new UpmGitUrl(host, owner, repo, cloneUrl,
            string.IsNullOrEmpty(refName) ? null : refName,
            string.IsNullOrEmpty(path) ? null : path);
    }
}
