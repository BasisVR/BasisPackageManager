namespace BasisPM.Core.Services;

/// <summary>
/// Keeps a mount's clone out of the outer repo's git status via <c>.git/info/exclude</c> — a per-clone,
/// untracked ignore list — so it never dirties a tracked <c>.gitignore</c> or rides into a package PR.
/// All operations are best-effort: a failed write only means a little <c>git status</c> noise.
/// </summary>
public static class GitExclude
{
    /// <param name="commonDirResolver">
    /// Optional resolver for the repo's common git dir (pass <c>GitService.GetCommonGitDir</c>). Used for
    /// linked worktrees, whose shared <c>info/exclude</c> is not under the per-worktree dir named by the
    /// on-disk <c>.git</c> pointer. Omitted (null) → filesystem-only resolution.
    /// </param>
    public static void Add(string repoRoot, string mountFolder, Func<string, string?>? commonDirResolver = null)
    {
        try
        {
            var pattern = PatternFor(repoRoot, mountFolder);
            var gitDir = ResolveGitDir(repoRoot, commonDirResolver);
            if (pattern is null || gitDir is null) return;

            var infoDir = Path.Combine(gitDir, "info");
            Directory.CreateDirectory(infoDir);
            var excludePath = Path.Combine(infoDir, "exclude");

            var lines = File.Exists(excludePath) ? File.ReadAllLines(excludePath).ToList() : new List<string>();
            if (lines.Any(l => l.Trim() == pattern)) return;
            lines.Add(pattern);
            File.WriteAllText(excludePath, string.Join('\n', lines) + '\n');
        }
        catch { }
    }

    /// <inheritdoc cref="Add(string,string,Func{string,string})"/>
    public static void Remove(string repoRoot, string mountFolder, Func<string, string?>? commonDirResolver = null)
    {
        try
        {
            var pattern = PatternFor(repoRoot, mountFolder);
            var gitDir = ResolveGitDir(repoRoot, commonDirResolver);
            if (pattern is null || gitDir is null) return;

            var excludePath = Path.Combine(gitDir, "info", "exclude");
            if (!File.Exists(excludePath)) return;

            var lines = File.ReadAllLines(excludePath).ToList();
            if (lines.RemoveAll(l => l.Trim() == pattern) > 0)
                File.WriteAllText(excludePath, string.Join('\n', lines) + '\n');
        }
        catch { }
    }

    /// <summary>
    /// A gitignore pattern anchored to the repo root, so <c>/Basis/Packages/&lt;id&gt;/</c> ignores only the
    /// mounted package, never all of <c>Packages</c>. Null when the folder isn't inside the repo.
    /// </summary>
    public static string? PatternFor(string repoRoot, string mountFolder)
    {
        var rel = Path.GetRelativePath(repoRoot, mountFolder).Replace('\\', '/').Trim('/');
        if (rel.Length == 0 || rel == "." || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal))
            return null;
        return "/" + rel + "/";
    }

    /// <summary>
    /// The git directory that owns <c>info/exclude</c> for this working tree:
    /// <list type="bullet">
    /// <item>Normal clone — the <c>.git</c> directory itself.</item>
    /// <item>Linked worktree / submodule (<c>.git</c> is a <c>gitdir:</c> pointer file) — the <b>common</b>
    /// git dir, since git reads a shared <c>info/exclude</c> there, not from the per-worktree dir the
    /// pointer names. We ask git via <paramref name="commonDirResolver"/>
    /// (<c>git rev-parse --git-common-dir</c>); if git can't answer, we resolve it off the filesystem.</item>
    /// </list>
    /// </summary>
    private static string? ResolveGitDir(string repoRoot, Func<string, string?>? commonDirResolver)
    {
        var dotGit = Path.Combine(repoRoot, ".git");
        if (Directory.Exists(dotGit)) return dotGit;   // normal clone — the common dir IS .git
        if (!File.Exists(dotGit)) return null;

        // Worktree/submodule pointer. Prefer git's authoritative common-dir; fall back to reading it
        // off the filesystem when git isn't available (GitExclude stays best-effort without git).
        var fromGit = commonDirResolver?.Invoke(repoRoot);
        if (!string.IsNullOrEmpty(fromGit) && Directory.Exists(fromGit)) return fromGit;

        return CommonDirFromPointer(repoRoot, dotGit);
    }

    /// <summary>
    /// Filesystem fallback for a <c>.git</c> pointer file. Resolves the <c>gitdir:</c> target, then — for
    /// a linked worktree — follows that dir's <c>commondir</c> file to the shared git dir. A submodule has
    /// no <c>commondir</c> file, so its pointed dir (which holds <c>info/exclude</c>) is returned as-is.
    /// </summary>
    private static string? CommonDirFromPointer(string repoRoot, string dotGitFile)
    {
        string? gitDir = null;
        foreach (var line in File.ReadAllLines(dotGitFile))
        {
            var t = line.Trim();
            if (t.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                var p = t["gitdir:".Length..].Trim();
                gitDir = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(repoRoot, p));
                break;
            }
        }
        if (gitDir is null || !Directory.Exists(gitDir)) return null;

        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
        {
            var target = File.ReadAllText(commonDirFile).Trim();
            if (target.Length > 0)
            {
                var common = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(gitDir, target));
                if (Directory.Exists(common)) return common;
            }
        }
        return gitDir;
    }
}
