using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class GitService
{
    private static readonly string[] WindowsGitPaths =
    {
        @"C:\Program Files\Git\cmd\git.exe",
        @"C:\Program Files\Git\bin\git.exe",
        @"C:\Program Files (x86)\Git\cmd\git.exe",
    };

    private string? _cachedGit;
    private readonly bool _allowLocalRemotes;

    public GitService(bool allowLocalRemotes = false) => _allowLocalRemotes = allowLocalRemotes;

    private string AllowedProtocols => _allowLocalRemotes ? GitUrlPolicy.AllowedGitProtocols + ":file" : GitUrlPolicy.AllowedGitProtocols;

    private bool IsFetchableUrl(string url) => GitUrlPolicy.IsSafeUrl(url) || (_allowLocalRemotes && Directory.Exists(url));

    public string? FindGit()
    {
        if (_cachedGit is not null && File.Exists(_cachedGit)) return _cachedGit;

        var onPath = ExecutableFinder.Locate("git");
        if (onPath is not null) return _cachedGit = onPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var found = WindowsGitPaths.FirstOrDefault(File.Exists);
            if (found is not null) return _cachedGit = found;
        }
        return null;
    }

    public bool IsAvailable => FindGit() is not null;

    public bool CanFetch(string? url) => !string.IsNullOrWhiteSpace(url) && IsFetchableUrl(url.Trim());

    public async Task<GitResult> CloneAsync(string url, string destPath, string? branch, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        if (!IsFetchableUrl(url))
            return new GitResult(false, -1, "Refused to clone: the URL uses an unsupported or unsafe git transport.");
        if (!GitUrlPolicy.IsSafeRef(branch))
            return new GitResult(false, -1, "Refused to clone: the branch name is not valid.");

        var args = new List<string> { "clone", "--progress" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch.Trim());
        }
        args.Add("--");            // no option may follow — url/dest are positional even if they start with '-'
        args.Add(url);
        args.Add(destPath);

        var (code, _, err) = await RunAsync(git, args, null, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    /// <summary>Clones, then checks out a specific ref — works for a branch, tag, OR commit (unlike clone --branch).</summary>
    public async Task<GitResult> CloneAtAsync(string url, string destPath, string? gitRef, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!GitUrlPolicy.IsSafeRef(gitRef))
            return new GitResult(false, -1, $"Refused to check out '{gitRef}': the ref is not valid.");
        var clone = await CloneAsync(url, destPath, null, onProgress, ct).ConfigureAwait(false);
        if (!clone.Ok || string.IsNullOrWhiteSpace(gitRef)) return clone;
        // Trailing "--" keeps a ref that collides with a filename from being taken as a pathspec.
        var (code, outText, err) = await RunGitAsync(destPath, new[] { "checkout", gitRef.Trim(), "--" }, onProgress, ct).ConfigureAwait(false);
        return code == 0 ? clone : new GitResult(false, code, $"Cloned, but couldn't check out '{gitRef}': {Combine(outText, err)}");
    }

    public async Task<GitResult> PullAsync(string repoRoot, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "pull", "--ff-only", "--progress" }, onProgress, ct).ConfigureAwait(false);
        var combined = string.Join('\n', new[] { outText.Trim(), err.Trim() }.Where(s => s.Length > 0));
        return new GitResult(code == 0, code, combined);
    }

    public async Task<GitResult> FetchAsync(string repoRoot, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var (code, _, err) = await RunGitAsync(repoRoot, new[] { "fetch", "--all", "--prune", "--progress" }, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    public async Task<string> GetBranchAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--abbrev-ref", "HEAD" }, null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Trim() : "unknown";
    }

    public async Task<string> GetShortCommitAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--short", "HEAD" }, null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Trim() : "";
    }

    public async Task<AheadBehind> GetAheadBehindAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--left-right", "--count", "HEAD...@{u}" }, null, ct).ConfigureAwait(false);
        if (code != 0) return AheadBehind.None;
        var parts = outText.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out var ahead) && int.TryParse(parts[1], out var behind))
            return new AheadBehind(true, ahead, behind);
        return AheadBehind.None;
    }

    public async Task<GitStatus> GetStatusAsync(string repoRoot, CancellationToken ct = default)
    {
        var branchTask = GetBranchAsync(repoRoot, ct);
        var commitTask = GetShortCommitAsync(repoRoot, ct);
        var upstreamTask = GetAheadBehindAsync(repoRoot, ct);
        var changes = await GetChangesAsync(repoRoot, ct).ConfigureAwait(false);
        return new GitStatus(await branchTask, await commitTask, changes, await upstreamTask);
    }

    /// <summary>Working-tree changes only (one <c>git status</c> call) — no branch/commit/upstream lookups.</summary>
    public async Task<IReadOnlyList<GitFileChange>> GetChangesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "status", "--porcelain=v1", "--untracked-files=all" }, null, ct).ConfigureAwait(false);
        var changes = new List<GitFileChange>();
        if (code == 0)
        {
            foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length < 4) continue;
                var xy = line[..2];
                var rest = line[3..].Trim();
                if (rest.Contains(" -> ", StringComparison.Ordinal))
                    rest = rest[(rest.IndexOf(" -> ", StringComparison.Ordinal) + 4)..];
                rest = rest.Trim('"');
                var staged = xy[0] is not ' ' and not '?';
                changes.Add(new GitFileChange(xy, rest, DetermineKind(xy), staged));
            }
            changes.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        }
        return changes;
    }

    public async Task<string> GetDiffAsync(string repoRoot, GitFileChange change, CancellationToken ct = default)
    {
        if (change.Kind == GitChangeKind.Untracked)
        {
            var (_, outUntracked, _) = await RunGitAsync(repoRoot,
                new[] { "diff", "--no-index", "--", NullDevice, change.Path }, null, ct).ConfigureAwait(false);
            return outUntracked.Length > 0 ? outUntracked : "(new file — no textual preview)";
        }

        var args = change.Staged
            ? new[] { "diff", "HEAD", "--", change.Path }
            : new[] { "diff", "--", change.Path };
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        if (outText.Trim().Length > 0) return outText;
        return code == 0 ? "(no textual changes)" : err.Trim();
    }

    public async Task<IReadOnlyList<string>> ListRemoteBranchesAsync(string url, CancellationToken ct = default)
    {
        var git = FindGit();
        if (git is null || !GitUrlPolicy.IsSafeUrl(url)) return Array.Empty<string>();
        var (code, outText, _) = await RunAsync(git, new[] { "ls-remote", "--heads", url }, null, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();
        var branches = new List<string>();
        foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf("refs/heads/", StringComparison.Ordinal);
            if (idx >= 0) branches.Add(line[(idx + "refs/heads/".Length)..].Trim());
        }
        return branches;
    }

    /// <summary>Remote tag names (annotated-tag peels dropped/deduped). Works for any host, no token needed.</summary>
    public async Task<IReadOnlyList<string>> ListRemoteTagsAsync(string url, CancellationToken ct = default)
    {
        var git = FindGit();
        if (git is null || !GitUrlPolicy.IsSafeUrl(url)) return Array.Empty<string>();
        var (code, outText, _) = await RunAsync(git, new[] { "ls-remote", "--tags", url }, null, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();
        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf("refs/tags/", StringComparison.Ordinal);
            if (idx < 0) continue;
            var tag = line[(idx + "refs/tags/".Length)..].Trim();
            if (tag.EndsWith("^{}", StringComparison.Ordinal)) tag = tag[..^3]; // annotated-tag peel
            if (tag.Length > 0 && seen.Add(tag)) tags.Add(tag);
        }
        return tags;
    }

    public bool IsGitRepo(string path)
    {
        try { return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")); }
        catch (Exception ex) { DiagnosticLog.Write($"Checking whether {path} is a Git repository", ex); return false; }
    }

    /// <summary>
    /// The repository's <b>common</b> git directory (absolute) via <c>git rev-parse --git-common-dir</c>.
    /// That's where shared state such as <c>info/exclude</c> lives, and — unlike the <c>.git</c> pointer
    /// inside a linked worktree, which names the per-worktree dir — it resolves to the shared dir. Null
    /// when git isn't available, <paramref name="repoRoot"/> isn't a repo, or the command fails.
    /// Runs synchronously: its one caller (best-effort <see cref="GitExclude"/>) is itself synchronous,
    /// and the output is a single short line so draining the pipes inline can't deadlock.
    /// </summary>
    public string? GetCommonGitDir(string repoRoot)
    {
        var git = FindGit();
        if (git is null || string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot)) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = git,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = repoRoot,
            };
            psi.ArgumentList.Add("rev-parse");
            psi.ArgumentList.Add("--git-common-dir");
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var p = Process.Start(psi);
            if (p is null) return null;
            var outText = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return null;

            var printed = outText.Trim();
            if (printed.Length == 0) return null;
            // git prints ".git" (or another relative path) for the main worktree and an absolute path
            // for a linked one; resolve against repoRoot so both become a concrete directory.
            var full = Path.IsPathRooted(printed) ? printed : Path.GetFullPath(Path.Combine(repoRoot, printed));
            return Directory.Exists(full) ? full : null;
        }
        catch (Exception ex) { DiagnosticLog.Write($"Resolving the common Git directory for {repoRoot}", ex); return null; }
    }

    // ---- write-side operations (publish wizard): init / add / commit / remote / push / tag ----

    public async Task<GitResult> InitAsync(string repoRoot, string defaultBranch = "main", CancellationToken ct = default)
    {
        Directory.CreateDirectory(repoRoot);
        var (code, _, err) = await RunGitAsync(repoRoot, new[] { "init", "-b", defaultBranch }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    public async Task<GitResult> AddAllAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, _, err) = await RunGitAsync(repoRoot, new[] { "add", "-A" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    /// <summary>Commits staged changes. Passes identity via -c so a fresh install with no global git config still works.</summary>
    public async Task<GitResult> CommitAsync(string repoRoot, string message, string? authorName = null, string? authorEmail = null, CancellationToken ct = default)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(authorName)) { args.Add("-c"); args.Add($"user.name={authorName.Trim()}"); }
        if (!string.IsNullOrWhiteSpace(authorEmail)) { args.Add("-c"); args.Add($"user.email={authorEmail.Trim()}"); }
        args.Add("commit");
        args.Add("-m");
        args.Add(message);
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    /// <summary>Creates and switches to a new branch (for the PR flow).</summary>
    public async Task<GitResult> CheckoutNewBranchAsync(string repoRoot, string branch, CancellationToken ct = default)
    {
        if (!GitUrlPolicy.IsSafeRef(branch))
            return new GitResult(false, -1, $"Refused to create branch '{branch}': the name is not valid.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "checkout", "-b", branch }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    /// <summary>Switches to an existing branch (a local branch, or a remote one git can check out and track).</summary>
    public async Task<GitResult> CheckoutAsync(string repoRoot, string branch, CancellationToken ct = default)
    {
        if (!GitUrlPolicy.IsSafeRef(branch))
            return new GitResult(false, -1, $"Refused to switch to '{branch}': the name is not valid.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "checkout", branch }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> CheckoutDetachedAsync(string repoRoot, string rev, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rev) || !GitUrlPolicy.IsSafeRef(rev))
            return new GitResult(false, -1, $"Refused to check out '{rev}': the ref is not valid.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "-c", "advice.detachedHead=false", "checkout", "--detach", rev.Trim(), "--" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    /// <summary>
    /// Branches this repo can switch to: local heads plus remote-tracking branches (remote prefix
    /// stripped, deduped against locals). Fetch first if you want branches added on the remote since clone.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListBranchesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot,
            new[] { "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes" }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var refName = line.Trim();
            string? branch = null;
            if (refName.StartsWith("refs/heads/", StringComparison.Ordinal))
                branch = refName["refs/heads/".Length..];
            else if (refName.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var rest = refName["refs/remotes/".Length..];        // "<remote>/<branch...>"
                var slash = rest.IndexOf('/');
                if (slash < 0) continue;
                branch = rest[(slash + 1)..];
                if (branch is "HEAD") continue;                       // the remote's symbolic default ref
            }
            if (!string.IsNullOrEmpty(branch) && seen.Add(branch)) names.Add(branch);
        }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    public async Task<string?> GetRemoteUrlAsync(string repoRoot, string remote = "origin", CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "remote", "get-url", remote }, null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Trim() : null;
    }

    /// <summary>Sets a local git config value (e.g. core.autocrlf) on a repo.</summary>
    public async Task<GitResult> SetConfigAsync(string repoRoot, string key, string value, CancellationToken ct = default)
    {
        var (code, _, err) = await RunGitAsync(repoRoot, new[] { "config", key, value }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    /// <summary>Adds the remote, or updates its URL when it already exists.</summary>
    public async Task<GitResult> SetRemoteAsync(string repoRoot, string remote, string url, CancellationToken ct = default)
    {
        var existing = await GetRemoteUrlAsync(repoRoot, remote, ct).ConfigureAwait(false);
        var args = existing is null ? new[] { "remote", "add", remote, url } : new[] { "remote", "set-url", remote, url };
        var (code, _, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    /// <summary><paramref name="remoteOrUrl"/> may be a token-embedded URL, so credentials never persist in .git/config.</summary>
    public async Task<GitResult> PushAsync(string repoRoot, string remoteOrUrl, string branch, bool setUpstream = false, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var args = new List<string> { "push", "--progress" };
        if (setUpstream) args.Add("-u");
        args.Add(remoteOrUrl);
        args.Add(branch);
        var (code, outText, err) = await RunGitAsync(repoRoot, args, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> TagAsync(string repoRoot, string tag, string? message = null, CancellationToken ct = default)
    {
        var args = string.IsNullOrWhiteSpace(message)
            ? new[] { "tag", tag }
            : new[] { "tag", "-a", tag, "-m", message };
        var (code, _, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    // ---- update / merge operations (Update Basis) ----

    public async Task<Version?> GetVersionAsync(CancellationToken ct = default)
    {
        var git = FindGit();
        if (git is null) return null;
        var (code, outText, _) = await RunAsync(git, new[] { "--version" }, null, null, ct).ConfigureAwait(false);
        return code == 0 ? ParseVersion(outText) : null;
    }

    public static Version? ParseVersion(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success) return null;
        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value), patch);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListRemoteHeadsAsync(string url, CancellationToken ct = default)
    {
        var heads = new Dictionary<string, string>(StringComparer.Ordinal);
        var git = FindGit();
        if (git is null || !IsFetchableUrl(url)) return heads;
        var (code, outText, _) = await RunAsync(git, new[] { "ls-remote", "--heads", "--", url }, null, null, ct).ConfigureAwait(false);
        if (code != 0) return heads;
        foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                heads[parts[1]["refs/heads/".Length..]] = parts[0];
        }
        return heads;
    }

    public async Task<GitResult> FetchBranchAsync(string repoRoot, string url, string branch, string localRef, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsFetchableUrl(url)) return new GitResult(false, -1, "Refused to fetch: the URL uses an unsupported or unsafe git transport.");
        if (string.IsNullOrWhiteSpace(branch) || !GitUrlPolicy.IsSafeRef(branch) || !localRef.StartsWith("refs/", StringComparison.Ordinal))
            return new GitResult(false, -1, $"Refused to fetch '{branch}': the branch name is not valid.");
        var args = new[] { "fetch", "--no-tags", "--progress", "--", url, $"+refs/heads/{branch.Trim()}:{localRef}" };
        var (code, outText, err) = await RunGitAsync(repoRoot, args, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> ResolveCommitAsync(string repoRoot, string rev, CancellationToken ct = default)
    {
        if (!GitUrlPolicy.IsSafeRef(rev)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--verify", "--quiet", $"{rev}^{{commit}}" }, null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<string?> GetCurrentBranchAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "symbolic-ref", "--quiet", "--short", "HEAD" }, null, ct).ConfigureAwait(false);
        var name = outText.Trim();
        return code == 0 && name.Length > 0 ? name : null;
    }

    public async Task<bool> IsAncestorAsync(string repoRoot, string ancestor, string descendant, CancellationToken ct = default)
    {
        var (code, _, _) = await RunGitAsync(repoRoot, new[] { "merge-base", "--is-ancestor", ancestor, descendant }, null, ct).ConfigureAwait(false);
        return code == 0;
    }

    public async Task<string?> GetMergeBaseAsync(string repoRoot, string a, string b, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "merge-base", a, b }, null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<int> CountCommitsAsync(string repoRoot, string range, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--count", range }, null, ct).ConfigureAwait(false);
        return code == 0 && int.TryParse(outText.Trim(), out var count) ? count : 0;
    }

    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoRoot, string range, int max, CancellationToken ct = default)
    {
        var args = new[] { "log", "--no-color", $"--max-count={max}", "--format=%H%x1f%h%x1f%an%x1f%at%x1f%s%x1e", range, "--" };
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        var commits = new List<GitCommitInfo>();
        if (code != 0) return commits;
        foreach (var record in outText.Split('\x1e'))
        {
            var fields = record.Trim('\r', '\n').Split('\x1f');
            if (fields.Length < 5 || fields[0].Length == 0) continue;
            var when = long.TryParse(fields[3], out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
            commits.Add(new GitCommitInfo(fields[0], fields[1], fields[2], when, fields[4]));
        }
        return commits;
    }

    public async Task<IReadOnlyList<string>> GetFirstParentHistoryAsync(string repoRoot, string rev, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--first-parent", rev }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<IReadOnlyList<string>> GetChangedPathsAsync(string repoRoot, string from, string to, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "diff", "--name-only", "--no-renames", "--no-ext-diff", "-z", from, to, "--" }, null, ct).ConfigureAwait(false);
        return code == 0 ? SplitNul(outText) : Array.Empty<string>();
    }

    public async Task<TreeDiffStats?> GetTreeDiffStatsAsync(string repoRoot, string from, string to, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "diff-tree", "-r", "--no-renames", "--name-status", "-z", from, to }, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        int added = 0, deleted = 0, modified = 0;
        var fields = SplitNul(outText);
        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            switch (fields[i][0])
            {
                case 'A': added++; break;
                case 'D': deleted++; break;
                default: modified++; break;
            }
        }
        return new TreeDiffStats(added, deleted, modified);
    }

    public async Task<int> CountTreeFilesAsync(string repoRoot, string treeish, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-tree", "-r", "--name-only", "-z", treeish }, null, ct).ConfigureAwait(false);
        return code == 0 ? SplitNul(outText).Count : 0;
    }

    public async Task<IReadOnlySet<string>> ListTrackedFilesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-files", "-z" }, null, ct).ConfigureAwait(false);
        return code == 0 ? new HashSet<string>(SplitNul(outText), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<GitWorkingEntry>> GetWorkingEntriesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "status", "--porcelain=v1", "-z", "--untracked-files=all" }, null, ct).ConfigureAwait(false);
        var entries = new List<GitWorkingEntry>();
        if (code != 0) return entries;
        var fields = SplitNul(outText);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (field.Length < 4) continue;
            var xy = field[..2];
            string? original = null;
            if ((xy[0] is 'R' or 'C') && i + 1 < fields.Count) original = fields[++i];
            entries.Add(new GitWorkingEntry(xy, field[3..], original));
        }
        return entries;
    }

    public async Task<IReadOnlyList<string>?> PredictConflictsAsync(string repoRoot, string ours, string theirs, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "merge-tree", "--write-tree", "--name-only", "--no-messages", ours, theirs }, null, ct).ConfigureAwait(false);
        if (code is not (0 or 1)) return null;
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<GitResult> MergeAsync(string repoRoot, string rev, string message, bool fastForwardOnly, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.AddRange(new[] { "merge", "--no-edit", "--no-overwrite-ignore", "--progress" });
        if (fastForwardOnly) args.Add("--ff-only");
        else { args.Add("-m"); args.Add(message); }
        args.Add(rev);
        var (code, outText, err) = await RunGitAsync(repoRoot, args, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> AbortMergeAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "merge", "--abort" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> ConcludeMergeAsync(string repoRoot, CancellationToken ct = default)
    {
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.AddRange(new[] { "commit", "--no-edit" });
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<IReadOnlyList<GitConflict>> GetConflictsAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-files", "-u", "-z" }, null, ct).ConfigureAwait(false);
        var stages = new Dictionary<string, (bool Base, bool Ours, bool Theirs)>(StringComparer.Ordinal);
        if (code != 0) return Array.Empty<GitConflict>();
        foreach (var record in SplitNul(outText))
        {
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 3) continue;
            var path = record[(tab + 1)..];
            stages.TryGetValue(path, out var s);
            stages[path] = meta[2] switch
            {
                "1" => s with { Base = true },
                "2" => s with { Ours = true },
                "3" => s with { Theirs = true },
                _ => s,
            };
        }
        return stages.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new GitConflict(kv.Key, kv.Value.Base, kv.Value.Ours, kv.Value.Theirs)).ToList();
    }

    public async Task<GitResult> TakeConflictSideAsync(string repoRoot, string path, bool ours, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "checkout", ours ? "--ours" : "--theirs", "--", Literal(path) }, null, ct).ConfigureAwait(false);
        if (code != 0) return new GitResult(false, code, Combine(outText, err));
        return await StagePathsAsync(repoRoot, new[] { path }, ct).ConfigureAwait(false);
    }

    public Task<GitResult> StagePathsAsync(string repoRoot, IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        RunWithPathsAsync(repoRoot, new[] { "add", "-A" }, paths, ct);

    public Task<GitResult> RemovePathsAsync(string repoRoot, IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        RunWithPathsAsync(repoRoot, new[] { "rm", "-q", "-f", "--ignore-unmatch" }, paths, ct);

    public Task<GitResult> UnstagePathsAsync(string repoRoot, IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        RunWithPathsAsync(repoRoot, new[] { "reset", "-q" }, paths, ct);

    public Task<GitResult> RestorePathsFromHeadAsync(string repoRoot, IReadOnlyCollection<string> paths, CancellationToken ct = default) =>
        RunWithPathsAsync(repoRoot, new[] { "checkout", "HEAD" }, paths, ct);

    public async Task<(GitResult Result, string? StashSha)> StashPathsAsync(string repoRoot, IReadOnlyCollection<string> paths, string message, CancellationToken ct = default)
    {
        var result = await RunWithPathsAsync(repoRoot, new[] { "stash", "push", "-q", "-m", message }, paths, ct).ConfigureAwait(false);
        if (!result.Ok) return (result, null);
        foreach (var (sha, subject) in await ListStashesAsync(repoRoot, ct).ConfigureAwait(false))
            if (subject.EndsWith(message, StringComparison.Ordinal)) return (result, sha);
        return (new GitResult(false, -1, "Nothing was set aside."), null);
    }

    public async Task<GitResult> ApplyStashAsync(string repoRoot, string stashSha, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "stash", "apply", stashSha }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> DropStashAsync(string repoRoot, string stashSha, CancellationToken ct = default)
    {
        var stashes = await ListStashesAsync(repoRoot, ct).ConfigureAwait(false);
        var index = stashes.Select(s => s.Sha).ToList().IndexOf(stashSha);
        if (index < 0) return new GitResult(true, 0, "");
        var (dropCode, dropOut, dropErr) = await RunGitAsync(repoRoot, new[] { "stash", "drop", "-q", $"stash@{{{index}}}" }, null, ct).ConfigureAwait(false);
        return new GitResult(dropCode == 0, dropCode, Combine(dropOut, dropErr));
    }

    public async Task<GitResult> UpdateRefAsync(string repoRoot, string refName, string newSha, string? expectedOldSha, string message, CancellationToken ct = default)
    {
        var args = new List<string> { "update-ref", "-m", message, refName, newSha };
        if (expectedOldSha is not null) args.Add(expectedOldSha);
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> DeleteRefAsync(string repoRoot, string refName, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "update-ref", "-d", refName }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> CommitTreeAsync(string repoRoot, string tree, IReadOnlyList<string> parents, string message, CancellationToken ct = default)
    {
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.Add("commit-tree");
        args.Add(tree);
        foreach (var parent in parents) { args.Add("-p"); args.Add(parent); }
        args.Add("-m");
        args.Add(message);
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<GitResult> ResetKeepAsync(string repoRoot, string commit, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "reset", "-q", "--keep", commit }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> CommitAllAsync(string repoRoot, string message, CancellationToken ct = default)
    {
        var add = await AddAllAsync(repoRoot, ct).ConfigureAwait(false);
        if (!add.Ok) return add;
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.AddRange(new[] { "commit", "-q", "-m", message });
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> GetTopLevelAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) || FindGit() is null) return null;
        var (code, outText, _) = await RunGitAsync(path, new[] { "rev-parse", "--show-toplevel" }, null, ct).ConfigureAwait(false);
        var top = outText.Trim();
        return code == 0 && top.Length > 0 ? Path.GetFullPath(top) : null;
    }

    public async Task<bool> IsTrackedInHeadAsync(string repoRoot, string relativePath, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-tree", "--name-only", "HEAD", "--", relativePath }, null, ct).ConfigureAwait(false);
        return code == 0 && outText.Trim().Length > 0;
    }

    public async Task<string?> GetGitDirAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--absolute-git-dir" }, null, ct).ConfigureAwait(false);
        var dir = outText.Trim();
        return code == 0 && dir.Length > 0 ? Path.GetFullPath(dir) : null;
    }

    public async Task<string?> GetOperationInProgressAsync(string repoRoot, CancellationToken ct = default)
    {
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (gitDir is null) return null;
        foreach (var (marker, operation) in OperationMarkers)
        {
            var path = Path.Combine(gitDir, marker);
            if (File.Exists(path) || Directory.Exists(path)) return operation;
        }
        return null;
    }

    public async Task<bool> IsMergeInProgressAsync(string repoRoot, CancellationToken ct = default)
    {
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        return gitDir is not null && File.Exists(Path.Combine(gitDir, "MERGE_HEAD"));
    }

    public async Task<bool> IsShallowAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--is-shallow-repository" }, null, ct).ConfigureAwait(false);
        return code == 0 && outText.Trim() == "true";
    }

    public async Task<string?> GetConfigValueAsync(string repoRoot, string key, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "config", "--get", key }, null, ct).ConfigureAwait(false);
        var value = outText.Trim();
        return code == 0 && value.Length > 0 ? value : null;
    }

    public async Task<IReadOnlyList<string>> ListRemoteUrlsAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "config", "--get-regexp", @"^remote\..*\.url$" }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => parts[1].Trim())
            .ToList();
    }

    public async Task<bool> IsIgnoredAsync(string repoRoot, string relativePath, CancellationToken ct = default)
    {
        var (code, _, _) = await RunGitAsync(repoRoot, new[] { "check-ignore", "-q", "--no-index", "--", relativePath }, null, ct).ConfigureAwait(false);
        return code == 0;
    }

    private static readonly (string Marker, string Operation)[] OperationMarkers =
    {
        ("MERGE_HEAD", "merge"),
        ("rebase-merge", "rebase"),
        ("rebase-apply", "rebase"),
        ("CHERRY_PICK_HEAD", "cherry-pick"),
        ("REVERT_HEAD", "revert"),
        ("BISECT_LOG", "bisect"),
    };

    private async Task<IReadOnlyList<(string Sha, string Subject)>> ListStashesAsync(string repoRoot, CancellationToken ct)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "reflog", "show", "--format=%H%x1f%gs", "refs/stash", "--" }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<(string, string)>();
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\x1f', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    private async Task<GitResult> RunWithPathsAsync(string repoRoot, IEnumerable<string> command, IReadOnlyCollection<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return new GitResult(true, 0, "");
        var args = command.Concat(new[] { "--pathspec-from-file=-", "--pathspec-file-nul" });
        var stdin = string.Join('\0', paths.Select(Literal)) + "\0";
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct, stdin).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    private async Task<List<string>> IdentityArgsAsync(string repoRoot, CancellationToken ct)
    {
        var args = new List<string>();
        if (await GetConfigValueAsync(repoRoot, "user.name", ct).ConfigureAwait(false) is null)
        {
            args.Add("-c");
            args.Add("user.name=Basis Package Manager");
        }
        if (await GetConfigValueAsync(repoRoot, "user.email", ct).ConfigureAwait(false) is null)
        {
            args.Add("-c");
            args.Add("user.email=basispm@localhost");
        }
        return args;
    }

    private static string Literal(string path) => ":(literal)" + path;

    private static List<string> SplitNul(string text) =>
        text.Split('\0').Select(s => s.Trim('\r', '\n')).Where(s => s.Length > 0).ToList();

    private static string Combine(string a, string b) =>
        string.Join('\n', new[] { a.Trim(), b.Trim() }.Where(s => s.Length > 0));

    private static string NullDevice => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "NUL" : "/dev/null";

    private static GitChangeKind DetermineKind(string xy)
    {
        if (xy == "??") return GitChangeKind.Untracked;
        if (xy.Contains('U') || xy is "AA" or "DD") return GitChangeKind.Conflicted;
        if (xy.Contains('R')) return GitChangeKind.Renamed;
        if (xy.Contains('D')) return GitChangeKind.Deleted;
        if (xy.Contains('A')) return GitChangeKind.Added;
        if (xy.Contains('M')) return GitChangeKind.Modified;
        return GitChangeKind.Other;
    }

    private Task<(int Code, string Stdout, string Stderr)> RunGitAsync(string repoRoot, IEnumerable<string> args, Action<string>? onProgress, CancellationToken ct, string? stdin = null)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        return RunAsync(git, args, repoRoot, onProgress, ct, stdin);
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, string? workingDir, Action<string>? onProgress, CancellationToken ct, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdin is not null) psi.StandardInputEncoding = new UTF8Encoding(false);
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("core.longpaths=true");
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        // Defence in depth: even if a hostile URL reaches git, only real fetch protocols are permitted.
        // This disables ext:: (arbitrary command execution) and file: at the git layer for every call.
        psi.Environment["GIT_ALLOW_PROTOCOL"] = AllowedProtocols;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stderr.AppendLine(e.Data);
            onProgress?.Invoke(e.Data);
        };

        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null)
        {
            await p.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
            p.StandardInput.Close();
        }
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
