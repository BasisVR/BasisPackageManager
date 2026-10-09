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
            return new AheadBehind(true, ahead, behind, await GetUpstreamNameAsync(repoRoot, ct).ConfigureAwait(false));
        return AheadBehind.None;
    }

    public async Task<string?> GetUpstreamNameAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{u}" }, null, ct).ConfigureAwait(false);
        var name = outText.Trim();
        return code == 0 && name.Length > 0 ? name : null;
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

    public Task<string?> GetMergeBaseAsync(string repoRoot, string a, string b, CancellationToken ct = default) =>
        GetMergeBaseAsync(repoRoot, a, new[] { b }, ct);

    public async Task<string?> GetMergeBaseAsync(string repoRoot, string a, IReadOnlyCollection<string> others, CancellationToken ct = default)
    {
        if (others.Count == 0) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "merge-base", a }.Concat(others), null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<IReadOnlyList<string>> ListRefsAsync(string repoRoot, IEnumerable<string> patterns, int max, CancellationToken ct = default)
    {
        var args = new List<string> { "for-each-ref", "--sort=-committerdate", $"--count={max}", "--format=%(refname)" };
        args.AddRange(patterns);
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
    }

    public async Task<string?> ShowFileAsync(string repoRoot, string rev, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rev) || !GitUrlPolicy.IsSafeRef(rev)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "show", $"{rev.Trim()}:{path}" }, null, ct).ConfigureAwait(false);
        return code == 0 ? outText : null;
    }

    public Task<int> CountCommitsAsync(string repoRoot, string range, CancellationToken ct = default) => CountCommitsAsync(repoRoot, range, null, ct);

    public async Task<int> CountCommitsAsync(string repoRoot, string range, string? path, CancellationToken ct = default)
    {
        var args = new List<string> { "rev-list", "--count", range, "--" };
        if (!string.IsNullOrEmpty(path)) args.Add(Literal(path));
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return code == 0 && int.TryParse(outText.Trim(), out var count) ? count : 0;
    }

    public Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoRoot, string range, int max, CancellationToken ct = default) => GetLogAsync(repoRoot, range, max, null, ct);

    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoRoot, string range, int max, string? path, CancellationToken ct = default)
    {
        var args = new List<string> { "log", "--no-color", $"--max-count={max}", "--format=%H%x1f%h%x1f%an%x1f%at%x1f%s%x1e", range, "--" };
        if (!string.IsNullOrEmpty(path)) args.Add(Literal(path));
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

    public Task<IReadOnlyList<string>> GetFirstParentHistoryAsync(string repoRoot, string rev, CancellationToken ct = default) => GetFirstParentHistoryAsync(repoRoot, rev, null, ct);

    public async Task<IReadOnlyList<string>> GetFirstParentHistoryAsync(string repoRoot, string rev, string? exclude, CancellationToken ct = default)
    {
        var args = new List<string> { "rev-list", "--first-parent", rev };
        if (!string.IsNullOrEmpty(exclude)) args.Add("^" + exclude);
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
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

    public async Task<string?> ResolveObjectAsync(string repoRoot, string spec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(spec) || spec.StartsWith('-') || spec.Any(char.IsControl)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--verify", "--quiet", spec }, null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && sha.Length > 0 ? sha : null;
    }

    public async Task<IReadOnlyList<string>?> ListTreeNamesAsync(string repoRoot, string treeish, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(treeish) || treeish.StartsWith('-') || treeish.Any(char.IsControl)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-tree", "-z", "--name-only", treeish }, null, ct).ConfigureAwait(false);
        return code == 0 ? SplitNul(outText) : null;
    }

    public async Task<IReadOnlyDictionary<string, string>?> ListTreeBlobsAsync(string repoRoot, string tree, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tree) || tree.StartsWith('-') || tree.Any(char.IsControl)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-tree", "-r", "-z", tree }, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in SplitNul(outText))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0) continue;
            var meta = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length == 3) blobs[entry[(tab + 1)..]] = meta[0] + " " + meta[2];
        }
        return blobs;
    }

    public async Task<string?> FindCommitWithTreeAsync(string repoRoot, string path, string tree, int maxCommits, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tree) || path.StartsWith('-') || path.Any(char.IsControl)) return null;
        var args = new List<string> { "rev-list", "--all", $"--max-count={maxCommits}" };
        if (path.Length > 0) { args.Add("--"); args.Add(path); }
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var commits = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commits.Length == 0) return null;
        var stdin = string.Join('\n', commits.Select(c => path.Length == 0 ? c + "^{tree}" : c + ":" + path)) + "\n";
        var (catCode, catOut, _) = await RunGitAsync(repoRoot, new[] { "cat-file", "--batch-check=%(objectname)" }, null, ct, stdin).ConfigureAwait(false);
        if (catCode != 0) return null;
        var lines = catOut.Split('\n', StringSplitOptions.TrimEntries);
        for (var i = 0; i < commits.Length && i < lines.Length; i++)
            if (string.Equals(lines[i], tree, StringComparison.OrdinalIgnoreCase)) return commits[i];
        return null;
    }

    public async Task<bool> IsPartialCloneAsync(string repoRoot, CancellationToken ct = default)
    {
        if (await GetConfigValueAsync(repoRoot, "extensions.partialclone", ct).ConfigureAwait(false) is not null) return true;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "config", "--get-regexp", @"^remote\..*\.promisor$" }, null, ct).ConfigureAwait(false);
        return code == 0 && outText.Split('\n').Any(line => line.TrimEnd().EndsWith(" true", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DateTimeOffset?> GetLastCommitTimeAsync(string repoRoot, string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('-') || path.Any(char.IsControl)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "log", "-1", "--format=%ct", "HEAD", "--", path }, null, ct).ConfigureAwait(false);
        return code == 0 && long.TryParse(outText.Trim(), out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
    }

    public async Task<IReadOnlySet<string>> ListTrackedFilesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-files", "-z" }, null, ct).ConfigureAwait(false);
        return code == 0 ? new HashSet<string>(SplitNul(outText), StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The ignore rule hiding each of the given paths, as (path, ignore file, pattern); paths nothing hides are left out.</summary>
    public async Task<IReadOnlyList<(string Path, string Source, string Pattern)>> ListIgnoreRulesAsync(string repoRoot, IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return Array.Empty<(string, string, string)>();
        var stdin = string.Concat(paths.Select(p => p + "\0"));
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "check-ignore", "-v", "-z", "--no-index", "--stdin" }, null, ct, stdin).ConfigureAwait(false);
        if (code > 1) return Array.Empty<(string, string, string)>();
        var fields = SplitNul(outText);
        var rules = new List<(string, string, string)>();
        for (var i = 0; i + 3 < fields.Count; i += 4)
            if (!fields[i + 2].StartsWith('!')) rules.Add((fields[i + 3], fields[i], fields[i + 2]));
        return rules;
    }

    /// <summary>Untracked, unignored files; a folder that's a git repository of its own is listed once, with a trailing '/'.</summary>
    public async Task<IReadOnlyList<string>?> ListUntrackedAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-files", "--others", "--exclude-standard", "-z" }, null, ct).ConfigureAwait(false);
        return code == 0 ? SplitNul(outText) : null;
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

    public Task<IReadOnlyList<string>?> PredictConflictsAsync(string repoRoot, string ours, string theirs, CancellationToken ct = default) =>
        PredictConflictsAsync(repoRoot, ours, theirs, null, ct);

    public async Task<IReadOnlyList<string>?> PredictConflictsAsync(string repoRoot, string ours, string theirs, string? mergeBase, CancellationToken ct = default)
    {
        var args = new List<string> { "merge-tree", "--write-tree", "--name-only", "--no-messages" };
        if (mergeBase is not null) args.Add("--merge-base=" + mergeBase);
        args.Add(ours);
        args.Add(theirs);
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        if (code is not (0 or 1)) return null;
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Skip(1).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<GitResult> MergeAsync(string repoRoot, string rev, string message, bool fastForwardOnly, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.AddRange(new[] { "merge", "--no-edit", "--no-overwrite-ignore", "--progress" });
        if (fastForwardOnly) args.Add("--ff-only");
        else { args.Add("--no-ff"); args.Add("-m"); args.Add(message); }
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

    /// <summary>
    /// Stashes the working-tree state of the given paths and takes those edits out of the checkout: tracked paths go back to
    /// HEAD in the index and the working tree, untracked files are deleted. Built from plumbing that reads paths on stdin,
    /// because <c>git stash push -- paths</c> hands every path to the git processes it starts as arguments, which fails with
    /// "Filename too long" on Windows once a few hundred paths are involved. A failed attempt leaves no stash behind.
    /// </summary>
    public async Task<(GitResult Result, string? StashSha)> SetAsidePathsAsync(string repoRoot, IReadOnlyCollection<string> tracked,
        IReadOnlyCollection<string> untracked, string message, CancellationToken ct = default)
    {
        var head = await ResolveCommitAsync(repoRoot, "HEAD", ct).ConfigureAwait(false);
        var headTree = head is null ? null : await ResolveTreeAsync(repoRoot, head, "", ct).ConfigureAwait(false);
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (head is null || headTree is null || gitDir is null) return (new GitResult(false, -1, "There's no commit to set edits aside from."), null);
        if (tracked.Concat(untracked).Any(p => p.Length == 0 || !GitUrlPolicy.IsSafeSubPath(p))) return (new GitResult(false, -1, "A path to set aside points outside the project."), null);

        var index = Path.Combine(gitDir, $"basispm-setaside-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        string tree;
        try
        {
            var (readCode, readOut, readErr) = await RunGitAsync(repoRoot, new[] { "read-tree", head }, null, ct, null, environment).ConfigureAwait(false);
            if (readCode != 0) return (new GitResult(false, readCode, Combine(readOut, readErr)), null);
            var stdin = string.Concat(tracked.Concat(untracked).Select(p => p + "\0"));
            var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "update-index", "--add", "--remove", "-z", "--stdin" }, null, ct, stdin, environment).ConfigureAwait(false);
            if (code != 0) return (new GitResult(false, code, Combine(outText, err)), null);
            var (treeCode, treeOut, treeErr) = await RunGitAsync(repoRoot, new[] { "write-tree" }, null, ct, null, environment).ConfigureAwait(false);
            tree = treeOut.Trim();
            if (treeCode != 0 || !IsObjectName(tree)) return (new GitResult(false, treeCode, Combine(treeOut, treeErr)), null);
        }
        finally { DeleteIndexFile(index); }

        // Shaped like an entry git stash makes (the working tree on top of HEAD, plus an index commit), so stash apply and
        // drop, and the user's own git tools, all treat it as one.
        var branch = await GetCurrentBranchAsync(repoRoot, ct).ConfigureAwait(false) ?? "(no branch)";
        var title = $"On {branch}: {message}";
        var indexCommit = await CommitTreeAsync(repoRoot, headTree, new[] { head }, $"index on {branch}: {head[..9]}", ct, unsigned: true).ConfigureAwait(false);
        var stash = indexCommit is null ? null : await CommitTreeAsync(repoRoot, tree, new[] { head, indexCommit }, title, ct, unsigned: true).ConfigureAwait(false);
        if (stash is null) return (new GitResult(false, -1, "Couldn't record the edits in a stash."), null);
        var (storeCode, storeOut, storeErr) = await RunGitAsync(repoRoot, new[] { "stash", "store", "-q", "-m", title, stash }, null, ct).ConfigureAwait(false);
        if (storeCode != 0) return (new GitResult(false, storeCode, Combine(storeOut, storeErr)), null);

        var removed = await RunWithPathsAsync(repoRoot, new[] { "restore", "--source=HEAD", "--staged", "--worktree" }, tracked, ct).ConfigureAwait(false);
        var stuck = removed.Ok ? untracked.Where(p => !TryDeleteWorkingFile(repoRoot, p)).ToList() : new List<string>();
        if (removed.Ok && stuck.Count == 0) return (removed, stash);

        // Bring back whatever already left the checkout, then drop the stash; it stays only if putting back fails too.
        var putBack = await RunWithPathsAsync(repoRoot, new[] { "restore", $"--source={stash}", "--worktree" }, tracked.Concat(untracked).ToList(), ct).ConfigureAwait(false);
        if (putBack.Ok) await DropStashAsync(repoRoot, stash, ct).ConfigureAwait(false);
        return (removed.Ok ? new GitResult(false, -1, $"Couldn't remove {string.Join(", ", stuck.Take(3))} to set it aside.") : removed, null);
    }

    public async Task<GitResult> UnstageAllAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "reset", "-q" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    /// <summary>Index entries that differ from HEAD (a staged deletion has mode 000000), for <see cref="RestageAsync"/> to put back.</summary>
    public async Task<IReadOnlyList<(string Mode, string Sha, string Path)>?> ListStagedEntriesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "diff-index", "--cached", "--no-renames", "-z", "HEAD" }, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var entries = new List<(string, string, string)>();
        var fields = SplitNul(outText);
        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            var meta = fields[i].Split(' ');
            if (meta.Length != 5 || !meta[0].StartsWith(':')) return null;
            entries.Add((meta[1], meta[3], fields[i + 1]));
        }
        return entries;
    }

    public async Task<GitResult> RestageAsync(string repoRoot, IReadOnlyCollection<(string Mode, string Sha, string Path)> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return new GitResult(true, 0, "");
        if (entries.Any(e => !IsObjectName(e.Sha) || !(IsFileMode(e.Mode) || e.Mode is "000000" or "160000") || !IsTreePath(e.Path)))
            return new GitResult(false, -1, "A staged edit to put back isn't valid.");
        var stdin = string.Concat(entries.Select(e => $"{e.Mode} {e.Sha}\t{e.Path}\0"));
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "update-index", "-z", "--index-info" }, null, ct, stdin).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    private static bool TryDeleteWorkingFile(string repoRoot, string path)
    {
        try
        {
            var full = Path.Combine(repoRoot, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Deleting {path} to set it aside", ex);
            return false;
        }
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

    public async Task<string?> CommitTreeAsync(string repoRoot, string tree, IReadOnlyList<string> parents, string message, CancellationToken ct = default, bool unsigned = false)
    {
        var args = await IdentityArgsAsync(repoRoot, ct).ConfigureAwait(false);
        args.Add("commit-tree");
        if (unsigned) args.Add("--no-gpg-sign");
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

    public async Task<IReadOnlyList<string>> ListRemoteUrlsAsync(string repoRoot, CancellationToken ct = default) =>
        (await ListRemotesAsync(repoRoot, ct).ConfigureAwait(false)).Select(remote => remote.Url).ToList();

    public async Task<IReadOnlyList<(string Name, string Url)>> ListRemotesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "config", "--get-regexp", @"^remote\..*\.url$" }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<(string, string)>();
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 2))
            .Where(parts => parts.Length == 2 && parts[0].Length > "remote..url".Length)
            .Select(parts => (parts[0]["remote.".Length..^".url".Length], parts[1].Trim()))
            .ToList();
    }

    public async Task<bool> IsIgnoredAsync(string repoRoot, string relativePath, CancellationToken ct = default)
    {
        var (code, _, _) = await RunGitAsync(repoRoot, new[] { "check-ignore", "-q", "--no-index", "--", relativePath }, null, ct).ConfigureAwait(false);
        return code == 0;
    }

    public async Task<GitResult> UnsetConfigAsync(string repoRoot, string key, CancellationToken ct = default)
    {
        var (code, _, err) = await RunGitAsync(repoRoot, new[] { "config", "--unset", key }, null, ct).ConfigureAwait(false);
        return new GitResult(code is 0 or 5, code, err.Trim());
    }

    public async Task<GitResult> FetchBranchesAsync(string repoRoot, string url, IReadOnlyCollection<string> branches, string refPrefix, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsFetchableUrl(url)) return new GitResult(false, -1, "Refused to fetch: the URL uses an unsupported or unsafe git transport.");
        if (branches.Count == 0 || !refPrefix.StartsWith("refs/", StringComparison.Ordinal) || branches.Any(b => string.IsNullOrWhiteSpace(b) || !GitUrlPolicy.IsSafeRef(b)))
            return new GitResult(false, -1, "Refused to fetch: a branch name is not valid.");
        var args = new List<string> { "fetch", "--no-tags", "--progress", "--", url };
        args.AddRange(branches.Select(b => $"+refs/heads/{b.Trim()}:{refPrefix}{b.Trim()}"));
        var (code, outText, err) = await RunGitAsync(repoRoot, args, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<IReadOnlyList<(string Name, string Sha)>> ListRefTargetsAsync(string repoRoot, IEnumerable<string> patterns, CancellationToken ct = default)
    {
        var args = new List<string> { "for-each-ref", "--format=%(refname)%09%(objectname)" };
        args.AddRange(patterns);
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<(string, string)>();
        return outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2)
            .Select(parts => (parts[0], parts[1]))
            .ToList();
    }

    public async Task<IReadOnlyList<string>> IndependentCommitsAsync(string repoRoot, IReadOnlyCollection<string> commits, CancellationToken ct = default)
    {
        if (commits.Count <= 1) return commits.ToList();
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "merge-base", "--independent" }.Concat(commits), null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
    }

    public async Task<(string Sha, string Message)?> FindLatestCommitMessageAsync(string repoRoot, string rev, string pattern, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "log", "-1", "--no-color", "--format=%H%x1f%B", "--grep=" + pattern, rev, "--" }, null, ct).ConfigureAwait(false);
        var parts = outText.Split('\x1f', 2);
        return code == 0 && parts.Length == 2 && IsObjectName(parts[0].Trim()) ? (parts[0].Trim(), parts[1]) : null;
    }

    public async Task<GitResult> CherryPickNoCommitAsync(string repoRoot, string commit, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsObjectName(commit)) return new GitResult(false, -1, "Refused to apply: not a commit id.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "cherry-pick", "--no-commit", commit }, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> WriteTreeAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "write-tree" }, null, ct).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && IsObjectName(sha) ? sha : null;
    }

    public async Task<string?> ResolveTreeAsync(string repoRoot, string commit, string folder, CancellationToken ct = default)
    {
        if (!IsObjectName(commit) || folder.StartsWith('-') || folder.Any(char.IsControl)) return null;
        var args = folder.Length == 0 ? new[] { "rev-parse", "--verify", "--quiet", commit + "^{tree}" } : new[] { "ls-tree", "-d", "-z", commit, "--", folder };
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        if (folder.Length == 0) return IsObjectName(outText.Trim()) ? outText.Trim() : null;
        foreach (var entry in SplitNul(outText))
        {
            var tab = entry.IndexOf('\t');
            var meta = tab < 0 ? Array.Empty<string>() : entry[..tab].Split(' ');
            if (meta.Length == 3 && meta[1] == "tree" && entry[(tab + 1)..] == folder) return meta[2];
        }
        return null;
    }

    public async Task<string?> MakeTreeAsync(string repoRoot, IReadOnlyList<(string Mode, string Type, string Sha, string Name)> entries, CancellationToken ct = default)
    {
        if (entries.Any(e => !IsObjectName(e.Sha) || e.Name.Length == 0 || e.Name.Contains('/') || e.Name.Any(char.IsControl))) return null;
        var stdin = string.Concat(entries.Select(e => $"{e.Mode} {e.Type} {e.Sha}\t{e.Name}\0"));
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "mktree", "-z" }, null, ct, stdin).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && IsObjectName(sha) ? sha : null;
    }

    public async Task<GitResult> ResetMergeAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "reset", "-q", "--merge" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> ResetIndexToHeadAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "read-tree", "--reset", "-u", "HEAD" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task SetMergeMessageAsync(string repoRoot, string message, CancellationToken ct = default)
    {
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (gitDir is null) return;
        try { await File.WriteAllTextAsync(Path.Combine(gitDir, "MERGE_MSG"), message.TrimEnd() + "\n", new UTF8Encoding(false), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Writing the merge message in {repoRoot}", ex); }
    }

    public async Task ClearMergeMessageAsync(string repoRoot, CancellationToken ct = default)
    {
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (gitDir is not null)
        {
            try { File.Delete(Path.Combine(gitDir, "MERGE_MSG")); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Removing the merge message in {repoRoot}", ex); }
        }
        await RunGitAsync(repoRoot, new[] { "update-ref", "-d", "AUTO_MERGE" }, null, ct).ConfigureAwait(false);
    }

    public async Task<GitResult> SwitchBranchAsync(string repoRoot, string branch, string? trackRef = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || !GitUrlPolicy.IsSafeRef(branch) || !GitUrlPolicy.IsSafeRef(trackRef))
            return new GitResult(false, -1, $"Refused to switch to '{branch}': the name is not valid.");
        var args = new List<string> { "checkout", "-q", "--no-overwrite-ignore" };
        if (string.IsNullOrWhiteSpace(trackRef)) args.Add(branch.Trim());
        else args.AddRange(new[] { "-b", branch.Trim(), "--track", trackRef.Trim() });
        args.Add("--");
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<(string? Tree, string Error)> SnapshotWorkingTreeAsync(string repoRoot, string? folder = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(folder) && !IsTreePath(folder)) return (null, $"'{folder}' isn't a folder of this repository.");
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (gitDir is null) return (null, $"{repoRoot} isn't a git repository.");
        var index = Path.Combine(gitDir, $"basispm-snapshot-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            var current = Path.Combine(gitDir, "index");
            if (File.Exists(current)) File.Copy(current, index);
            else if (await ResolveCommitAsync(repoRoot, "HEAD", ct).ConfigureAwait(false) is not null)
            {
                var (readCode, readOut, readErr) = await RunGitAsync(repoRoot, new[] { "read-tree", "HEAD" }, null, ct, null, environment).ConfigureAwait(false);
                if (readCode != 0) return (null, Combine(readOut, readErr));
            }
            var args = new List<string> { "add", "-A" };
            if (!string.IsNullOrEmpty(folder)) args.AddRange(new[] { "--", Literal(folder) });
            var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct, null, environment).ConfigureAwait(false);
            if (code != 0) return (null, Combine(outText, err));
            var (treeCode, tree, treeErr) = await RunGitAsync(repoRoot, new[] { "write-tree" }, null, ct, null, environment).ConfigureAwait(false);
            var sha = tree.Trim();
            return treeCode == 0 && IsObjectName(sha) ? (sha, "") : (null, treeErr.Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading the working tree of {repoRoot}", ex);
            return (null, ex.Message);
        }
        finally { DeleteIndexFile(index); }
    }

    public async Task<IReadOnlyList<GitTreeChange>?> DiffTreesAsync(string repoRoot, string from, string to, CancellationToken ct = default)
    {
        if (!IsObjectName(from) || !IsObjectName(to)) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "diff-tree", "-r", "-z", "--raw", "--no-renames", from, to }, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var changes = new List<GitTreeChange>();
        var fields = SplitNul(outText);
        for (var i = 0; i + 1 < fields.Count; i += 2)
        {
            var meta = fields[i].Split(' ');
            if (meta.Length != 5 || !meta[0].StartsWith(':') || meta[4].Length == 0) return null;
            changes.Add(new GitTreeChange(meta[0][1..], meta[1], meta[2], meta[3], meta[4][0], fields[i + 1]));
        }
        return changes;
    }

    public async Task<string?> BuildTreeAsync(string repoRoot, string baseCommit, IReadOnlyCollection<(string Mode, string Sha, string Path)> files, IReadOnlyCollection<string> deletions, CancellationToken ct = default)
    {
        if (!IsObjectName(baseCommit) || files.Any(f => !IsObjectName(f.Sha) || !IsFileMode(f.Mode) || !IsTreePath(f.Path)) || deletions.Any(p => !IsTreePath(p))) return null;
        var gitDir = await GetGitDirAsync(repoRoot, ct).ConfigureAwait(false);
        if (gitDir is null) return null;
        var index = Path.Combine(gitDir, $"basispm-tree-{Guid.NewGuid():N}.index");
        var environment = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
        try
        {
            var (readCode, _, _) = await RunGitAsync(repoRoot, new[] { "read-tree", baseCommit }, null, ct, null, environment).ConfigureAwait(false);
            if (readCode != 0) return null;
            var zero = new string('0', baseCommit.Length);
            var stdin = string.Concat(deletions.Select(path => $"0 {zero}\t{path}\0").Concat(files.Select(f => $"{f.Mode} {f.Sha}\t{f.Path}\0")));
            var (code, _, _) = await RunGitAsync(repoRoot, new[] { "update-index", "-z", "--index-info" }, null, ct, stdin, environment).ConfigureAwait(false);
            if (code != 0) return null;
            var (treeCode, tree, _) = await RunGitAsync(repoRoot, new[] { "write-tree" }, null, ct, null, environment).ConfigureAwait(false);
            var sha = tree.Trim();
            return treeCode == 0 && IsObjectName(sha) ? sha : null;
        }
        finally { DeleteIndexFile(index); }
    }

    public async Task<string?> CommitTreeAsAsync(string repoRoot, string tree, string parent, string message, string name, string email, CancellationToken ct = default)
    {
        if (!IsObjectName(tree) || !IsObjectName(parent) || string.IsNullOrWhiteSpace(message)
            || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || (name + email).Any(char.IsControl)) return null;
        var environment = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = name.Trim(),
            ["GIT_AUTHOR_EMAIL"] = email.Trim(),
            ["GIT_COMMITTER_NAME"] = name.Trim(),
            ["GIT_COMMITTER_EMAIL"] = email.Trim(),
        };
        var args = new[] { "commit-tree", "--no-gpg-sign", tree, "-p", parent, "-F", "-" };
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct, message.Trim() + "\n", environment).ConfigureAwait(false);
        var sha = outText.Trim();
        return code == 0 && IsObjectName(sha) ? sha : null;
    }

    public async Task<bool> IsValidBranchNameAsync(string repoRoot, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(branch) || !GitUrlPolicy.IsSafeRef(branch)) return false;
        var (code, _, _) = await RunGitAsync(repoRoot, new[] { "check-ref-format", "--branch", branch.Trim() }, null, ct).ConfigureAwait(false);
        return code == 0;
    }

    public async Task<IReadOnlyDictionary<string, string>?> ListRemoteRefsAsync(string repoRoot, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || url.TrimStart().StartsWith('-')) return null;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "ls-remote", "--heads", "--", url.Trim() }, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var refs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in outText.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split('\t');
            if (parts.Length == 2 && IsObjectName(parts[0])) refs[parts[1]] = parts[0];
        }
        return refs;
    }

    public async Task<GitPushEstimate?> EstimatePushAsync(string repoRoot, string commit, IEnumerable<string> remoteCommits, CancellationToken ct = default)
    {
        if (!IsObjectName(commit)) return null;
        var stdin = commit + "\n" + string.Concat(remoteCommits.Where(IsObjectName).Distinct(StringComparer.OrdinalIgnoreCase).Select(sha => "^" + sha + "\n"));
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--count", "--ignore-missing", "--stdin" }, null, ct, stdin).ConfigureAwait(false);
        if (code != 0 || !int.TryParse(outText.Trim(), out var commits)) return null;
        long? bytes = null;
        if (await GetVersionAsync(ct).ConfigureAwait(false) is { } version && version >= DiskUsageGitVersion)
        {
            var (sizeCode, sizeText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--objects", "--disk-usage", "--ignore-missing", "--stdin" }, null, ct, stdin).ConfigureAwait(false);
            if (sizeCode == 0 && long.TryParse(sizeText.Trim(), out var size)) bytes = size;
        }
        return new GitPushEstimate(commits, bytes);
    }

    public async Task<GitResult> PushCommitAsync(string repoRoot, string url, string commit, string branch, string? expectedRemoteSha, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsObjectName(commit) || string.IsNullOrWhiteSpace(branch) || !GitUrlPolicy.IsSafeRef(branch) || string.IsNullOrWhiteSpace(url) || url.TrimStart().StartsWith('-')
            || (expectedRemoteSha is not null && !IsObjectName(expectedRemoteSha)))
            return new GitResult(false, -1, "Refused to push: the branch or commit is not valid.");
        var target = "refs/heads/" + branch.Trim();
        var args = new List<string> { "push", "--progress" };
        if (expectedRemoteSha is not null) args.Add($"--force-with-lease={target}:{expectedRemoteSha}");
        args.Add(url.Trim());
        args.Add($"{commit}:{target}");
        var (code, outText, err) = await RunGitAsync(repoRoot, args, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> DiffPathAsync(string repoRoot, string fromTree, string toTree, string path, CancellationToken ct = default)
    {
        if (!IsObjectName(fromTree) || !IsObjectName(toTree) || !IsTreePath(path)) return null;
        var args = new[] { "diff", "--no-color", "--no-ext-diff", "--no-textconv", "--no-renames", "-U3", fromTree, toTree, "--", Literal(path) };
        var (code, outText, _) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return code == 0 ? outText : null;
    }

    public async Task<bool> HasUnpublishedWorkAsync(string repoRoot, CancellationToken ct = default)
    {
        var (stash, _, _) = await RunGitAsync(repoRoot, new[] { "rev-parse", "--verify", "--quiet", "refs/stash" }, null, ct).ConfigureAwait(false);
        if (stash == 0) return true;
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "rev-list", "--count", "HEAD", "--branches", "--not", "--remotes", "--tags" }, null, ct).ConfigureAwait(false);
        return code != 0 || !int.TryParse(outText.Trim(), out var count) || count > 0;
    }

    private static readonly Version DiskUsageGitVersion = new(2, 31, 0);

    private static bool IsFileMode(string mode) => mode is "100644" or "100755" or "120000";

    private static bool IsTreePath(string path) =>
        path.Length > 0 && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part is not ("." or "..") && !part.Equals(".git", StringComparison.OrdinalIgnoreCase));

    private static void DeleteIndexFile(string index)
    {
        foreach (var file in new[] { index, index + ".lock" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Removing the temporary index {file}", ex); }
        }
    }

    private static bool IsObjectName(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

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

    private Task<(int Code, string Stdout, string Stderr)> RunGitAsync(string repoRoot, IEnumerable<string> args, Action<string>? onProgress, CancellationToken ct, string? stdin = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        return RunAsync(git, args, repoRoot, onProgress, ct, stdin, environment);
    }

    private async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, string? workingDir, Action<string>? onProgress, CancellationToken ct, string? stdin = null, IReadOnlyDictionary<string, string>? environment = null)
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
        if (environment is not null)
            foreach (var (name, value) in environment) psi.Environment[name] = value;

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
        using var stop = IsNetworkCommand(args) ? ct.Register(() => Kill(p)) : default;
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

    private static bool IsNetworkCommand(IEnumerable<string> args)
    {
        using var arg = args.GetEnumerator();
        while (arg.MoveNext())
        {
            if (arg.Current == "-c") { arg.MoveNext(); continue; }
            if (arg.Current.StartsWith('-')) continue;
            return arg.Current is "clone" or "fetch" or "ls-remote" or "push";
        }
        return false;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { DiagnosticLog.Write("Stopping a cancelled git command", ex); }
    }
}
