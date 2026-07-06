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

    public async Task<GitResult> CloneAsync(string url, string destPath, string? branch, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        if (!GitUrlPolicy.IsSafeUrl(url))
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

        return new GitStatus(await branchTask, await commitTask, changes, await upstreamTask);
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
        catch { return false; }
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
        catch { return null; }
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

    private Task<(int Code, string Stdout, string Stderr)> RunGitAsync(string repoRoot, IEnumerable<string> args, Action<string>? onProgress, CancellationToken ct)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        return RunAsync(git, args, repoRoot, onProgress, ct);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, string? workingDir, Action<string>? onProgress, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Defence in depth: even if a hostile URL reaches git, only real fetch protocols are permitted.
        // This disables ext:: (arbitrary command execution) and file: at the git layer for every call.
        psi.Environment["GIT_ALLOW_PROTOCOL"] = GitUrlPolicy.AllowedGitProtocols;

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
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
