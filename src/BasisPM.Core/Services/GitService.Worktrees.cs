using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed partial class GitService
{
    public async Task<GitResult> CloneBareAsync(string url, string destPath, bool blobless, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var git = FindGit() ?? throw new InvalidOperationException("Git was not found. Install Git and make sure it is on your PATH.");
        if (!IsFetchableUrl(url)) return new GitResult(false, -1, "Refused to clone: the URL uses an unsupported or unsafe git transport.");
        var options = new List<string> { "--bare", "--progress" };
        if (blobless && !Directory.Exists(url)) options.Add("--filter=blob:none");
        var (code, outText, err) = await RunAsync(git, CloneArgs(options, url, destPath), null, onProgress, ct).ConfigureAwait(false);
        if (code != 0) return new GitResult(false, code, Combine(outText, err));
        foreach (var (key, value) in new[] { ("extensions.worktreeConfig", "true"), ("remote.origin.fetch", "+refs/heads/*:refs/heads/*") })
        {
            var set = await SetConfigAsync(destPath, key, value, ct).ConfigureAwait(false);
            if (!set.Ok) return set;
        }
        return new GitResult(true, 0, err.Trim());
    }

    public async Task<GitResult> FetchRemoteAsync(string repoRoot, string remote, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsRemoteName(remote)) return new GitResult(false, -1, $"Refused to fetch '{remote}': the remote name is not valid.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "fetch", "--tags", "--progress", "--", remote }, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> FetchCommitAsync(string repoRoot, string remote, string commit, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsRemoteName(remote) || !IsObjectName(commit)) return new GitResult(false, -1, "Refused to fetch: the remote or commit is not valid.");
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "fetch", "--progress", "--", remote, commit }, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<string?> ResolveRemoteRefAsync(string url, string? gitRef, CancellationToken ct = default)
    {
        var git = FindGit();
        if (git is null || !IsFetchableUrl(url) || !GitUrlPolicy.IsSafeRef(gitRef)) return null;
        var wanted = string.IsNullOrWhiteSpace(gitRef) ? "HEAD" : gitRef.Trim();
        var (code, outText, _) = await RunAsync(git, new[] { "ls-remote", "--", url, wanted, wanted + "^{}" }, null, null, ct).ConfigureAwait(false);
        if (code != 0) return null;
        var rows = outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('\t')).Where(p => p.Length == 2).ToList();
        string? Pick(Func<string, bool> match) => rows.FirstOrDefault(r => match(r[1]))?[0];
        return Pick(n => n == "refs/tags/" + wanted + "^{}") ?? Pick(n => n == "refs/tags/" + wanted) ?? Pick(n => n == "refs/heads/" + wanted) ?? Pick(n => n == wanted) ?? rows.FirstOrDefault()?[0];
    }

    public async Task<GitResult> AddWorktreeAsync(string repoRoot, string folder, string? detachAt, string? branch, string? startPoint, CancellationToken ct = default)
    {
        if (!GitUrlPolicy.IsSafeRef(detachAt) || !GitUrlPolicy.IsSafeRef(branch) || !GitUrlPolicy.IsSafeRef(startPoint)) return new GitResult(false, -1, "Refused to add a worktree: a ref is not valid.");
        var args = new List<string> { "worktree", "add", "--no-checkout" };
        if (!string.IsNullOrWhiteSpace(detachAt)) args.AddRange(new[] { "--detach", "--", Path.GetFullPath(folder), detachAt.Trim() });
        else if (!string.IsNullOrWhiteSpace(startPoint)) args.AddRange(new[] { "-b", branch!.Trim(), "--", Path.GetFullPath(folder), startPoint.Trim() });
        else args.AddRange(new[] { "--", Path.GetFullPath(folder), branch!.Trim() });
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> SetWorktreeConfigAsync(string worktree, string key, string value, CancellationToken ct = default)
    {
        var (code, _, err) = await RunGitAsync(worktree, new[] { "config", "--worktree", key, value }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
    }

    public async Task<GitResult> SetConeSparseCheckoutAsync(string worktree, IReadOnlyCollection<string> folders, CancellationToken ct = default)
    {
        if (folders.Any(f => string.IsNullOrWhiteSpace(f) || f.Any(char.IsControl) || !GitUrlPolicy.IsSafeSubPath(f) || f.StartsWith('-')))
            return new GitResult(false, -1, "Refused to change which folders are checked out: a folder is not valid.");
        var (code, outText, err) = await RunGitAsync(worktree, new[] { "sparse-checkout", "set", "--cone", "--stdin" }, null, ct, string.Join('\n', folders.Select(f => f.Replace(Path.DirectorySeparatorChar, '/').Trim('/'))) + "\n").ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<IReadOnlyList<string>> GetConeSparseFoldersAsync(string worktree, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(worktree, new[] { "sparse-checkout", "list" }, null, ct).ConfigureAwait(false);
        return code == 0 ? outText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
    }

    public async Task<GitResult> HardResetAsync(string worktree, string commit, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!IsObjectName(commit)) return new GitResult(false, -1, "Refused to check out: the commit is not valid.");
        var (code, outText, err) = await RunGitAsync(worktree, new[] { "reset", "-q", "--hard", commit }, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<IReadOnlyList<string>> ListWorktreesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, _) = await RunGitAsync(repoRoot, new[] { "worktree", "list", "--porcelain" }, null, ct).ConfigureAwait(false);
        if (code != 0) return Array.Empty<string>();
        return outText.Split('\n').Where(l => l.StartsWith("worktree ", StringComparison.Ordinal)).Select(l => l[9..].Trim()).ToList();
    }

    public async Task<GitResult> PruneWorktreesAsync(string repoRoot, CancellationToken ct = default)
    {
        var (code, outText, err) = await RunGitAsync(repoRoot, new[] { "worktree", "prune" }, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    public async Task<GitResult> PrefetchBlobsAsync(string repoRoot, IReadOnlyCollection<string> blobs, CancellationToken ct = default)
    {
        var wanted = blobs.Where(IsObjectName).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0 || !await IsPartialCloneAsync(repoRoot, ct).ConfigureAwait(false)) return new GitResult(true, 0, "");
        var (checkCode, present, _) = await RunGitAsync(repoRoot, new[] { "--no-lazy-fetch", "cat-file", "--batch-check=%(objectname) %(objecttype)" }, null, ct, string.Join('\n', wanted) + "\n").ConfigureAwait(false);
        var missing = checkCode == 0 ? present.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.EndsWith(" missing", StringComparison.Ordinal)).Select(l => l.Split(' ')[0]).ToList() : wanted;
        if (missing.Count == 0) return new GitResult(true, 0, "");
        var args = new List<string> { "-c", "fetch.negotiationAlgorithm=noop", "fetch", "-q", "--no-tags", "--no-write-fetch-head", "--recurse-submodules=no", "--filter=blob:none", "origin" };
        args.AddRange(missing);
        var (code, outText, err) = await RunGitAsync(repoRoot, args, null, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, Combine(outText, err));
    }

    private static bool IsRemoteName(string? name) => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('-') && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
}
