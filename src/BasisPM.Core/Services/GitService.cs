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

        var onPath = WhichOnPath(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "git.exe" : "git");
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
        var args = new List<string> { "clone", "--progress" };
        if (!string.IsNullOrWhiteSpace(branch))
        {
            args.Add("--branch");
            args.Add(branch.Trim());
        }
        args.Add(url);
        args.Add(destPath);

        var (code, _, err) = await RunAsync(git, args, null, onProgress, ct).ConfigureAwait(false);
        return new GitResult(code == 0, code, err.Trim());
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
        if (git is null) return Array.Empty<string>();
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

    public bool IsGitRepo(string path)
    {
        try { return Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")); }
        catch { return false; }
    }

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
        };
        if (!string.IsNullOrEmpty(workingDir)) psi.WorkingDirectory = workingDir;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

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

    private static string? WhichOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
