using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

/// <summary>
/// Lists the installable versions of a package's git repo, preferring published Releases, then git tags,
/// then the default branch — and distinguishing stable vs prerelease. Degrades gracefully; never throws.
/// </summary>
public sealed class VersionService(GitHubApiService? github = null, GitService? git = null)
{
    private readonly GitHubApiService _github = github ?? new GitHubApiService();
    private readonly GitService _git = git ?? new GitService();

    public async Task<PackageVersions> GetVersionsAsync(string? gitUrl, string? token = null, CancellationToken ct = default)
    {
        var loc = UpmGitUrl.Parse(gitUrl);
        if (loc is null) return PackageVersions.Empty;

        var options = new List<PackageVersionOption>();

        // 1. Preferred: published GitHub releases (authoritative prerelease flags).
        if (loc.IsGitHub)
        {
            IReadOnlyList<GitHubRelease> list = await _github.GetReleasesAsync(loc.Owner, loc.Repo, token, ct).ConfigureAwait(false);
            for (int i = 0; i < list.Count; i++)
            {
                GitHubRelease? r = list[i];
                if (r.Draft || string.IsNullOrWhiteSpace(r.TagName) || !GitUrlPolicy.IsSafeRef(r.TagName))
                {
                    continue;
                }

                string label = string.IsNullOrWhiteSpace(r.Name) || r.Name == r.TagName ? r.TagName : $"{r.TagName}  ({r.Name})";
                options.Add(new PackageVersionOption(r.TagName, label, r.Prerelease, VersionKind.Release));
            }
        }
        var hasReleases = options.Count > 0;

        // 2. Fallback: git tags (any host, no token). Classify prerelease from the semver suffix.
        if (!hasReleases)
        {
            IReadOnlyList<string> list = await _git.ListRemoteTagsAsync(loc.CloneUrl, ct).ConfigureAwait(false);
            for (int i = 0; i < list.Count; i++)
            {
                string? t = list[i];
                if (!GitUrlPolicy.IsSafeRef(t))
                {
                    continue;
                }

                bool pre = SemVer.TryParseTag(t, out var sv) && sv.PreRelease is not null;
                options.Add(new PackageVersionOption(t, t, pre, VersionKind.Tag));
            }
        }

        var sorted = SortBySemVer(options);

        // 3. Always offer "the project itself" — the default branch (no #ref → UPM uses the repo HEAD).
        sorted.Add(new PackageVersionOption(null, "Latest (default branch, unreleased)", false, VersionKind.Branch));

        return new PackageVersions(sorted, hasReleases);
    }

    // Newest-first by semver; unparseable refs keep their original order at the end.
    private static List<PackageVersionOption> SortBySemVer(List<PackageVersionOption> options)
    {
        var keyed = new List<(PackageVersionOption o, SemVer? v, int i)>(options.Count);
        for (var i = 0; i < options.Count; i++)
        {
            var ok = SemVer.TryParseTag(options[i].Ref, out var v);
            keyed.Add((options[i], ok ? v : null, i));
        }
        keyed.Sort((a, b) =>
        {
            if (a.v is not null && b.v is not null)
            {
                return b.v.CompareTo(a.v); // stable sorts above its prerelease
            }

            return a.v is not null ? -1 : b.v is not null ? 1 : a.i.CompareTo(b.i);
        });
        return keyed.ConvertAll(k => k.o);
    }
}
