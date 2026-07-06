using System.Text.RegularExpressions;

namespace BasisPM.Core.Services;

/// <summary>
/// Central validation for the untrusted URLs, refs and sub-paths that flow in from deep links,
/// the package registry, package lists and Unity manifests before they reach <c>git</c> or get written
/// back into a project. The goal is to block:
/// <list type="bullet">
///   <item>git's remote-command transports — above all <c>ext::</c>, which runs an arbitrary shell
///   command, and <c>file:</c> — so a hostile package URL can't become code execution;</item>
///   <item>option injection, where a value beginning with <c>-</c> is read by git as a flag
///   (e.g. <c>--upload-pack=…</c>) instead of a URL/ref;</item>
///   <item>path traversal out of a clone via a crafted <c>?path=</c> sub-folder.</item>
/// </list>
/// </summary>
public static class GitUrlPolicy
{
    // Fetch protocols that only move data. Anything else — ext, file, ftp, … — is refused.
    // This exact string is also handed to git as GIT_ALLOW_PROTOCOL so the block is enforced twice.
    public const string AllowedGitProtocols = "https:http:git:ssh";

    private static readonly string[] AllowedSchemes = { "https://", "http://", "ssh://", "git://" };

    // scp-style shorthand only: git@host:owner/repo(.git). Host is a plain DNS name, path has no spaces.
    private static readonly Regex ScpStyle =
        new(@"^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[^\s]+$", RegexOptions.Compiled);

    /// <summary>
    /// True for a git URL safe to clone or to store as a manifest dependency: an explicit fetch-protocol
    /// URL (https/http/ssh/git) or scp shorthand, with no control/whitespace characters and no leading
    /// <c>-</c>. Rejects <c>ext::</c>, <c>file:</c>, <c>javascript:</c>, bare local paths, etc.
    /// </summary>
    public static bool IsSafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var u = url.Trim();

        if (u.StartsWith('-')) return false;                    // don't let git read it as an option
        if (u.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return false;

        if (u.Contains("://", StringComparison.Ordinal))
            return AllowedSchemes.Any(s => u.StartsWith(s, StringComparison.OrdinalIgnoreCase));

        return ScpStyle.IsMatch(u);                             // git@host:owner/repo — no other schemeless form
    }

    /// <summary>
    /// Stricter form for the public registry / website links: an absolute <c>https://</c> URL whose host
    /// is github.com or gitlab.com. Keeps ssh/scp and arbitrary hosts out of a hosted catalog.
    /// </summary>
    public static bool IsHostedGitUrl(string? url)
    {
        if (!IsSafeUrl(url)) return false;
        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        return IsAllowedHost(u.Host);
    }

    /// <summary>An absolute http/https/mailto URL — safe to hand to the OS "open in browser" handler.</summary>
    public static bool IsWebUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto);

    /// <summary>A git ref (branch/tag/commit) that can't be misread as a command-line option.</summary>
    public static bool IsSafeRef(string? gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef)) return true;     // "no ref" is fine
        var r = gitRef.Trim();
        if (r.StartsWith('-')) return false;
        return !r.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
    }

    /// <summary>A UPM sub-path (<c>?path=</c>) that stays inside the clone: relative, no <c>..</c>, no NUL.</summary>
    public static bool IsSafeSubPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        if (path.Any(char.IsControl)) return false;
        if (Path.IsPathRooted(path)) return false;
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.All(p => p != "..");
    }

    private static bool IsAllowedHost(string host)
    {
        host = host.ToLowerInvariant();
        return host is "github.com" or "www.github.com" or "gitlab.com" or "www.gitlab.com";
    }
}
