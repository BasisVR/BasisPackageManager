using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class ProjectTargetService
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly GitService _git;
    private readonly BasisUpdateService _basis;
    private readonly GitHubApiService _github;
    private readonly Dictionary<string, (ProjectTarget Target, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset?> _commitDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ProjectTargetService(GitService git, BasisUpdateService basis, GitHubApiService? github = null)
    {
        _git = git;
        _basis = basis;
        _github = github ?? new GitHubApiService();
    }

    public void Invalidate(string? repoRoot = null)
    {
        lock (_gate)
        {
            if (repoRoot is null) _cache.Clear();
            else _cache.Remove(repoRoot);
        }
    }

    public async Task<ProjectTarget> ResolveAsync(BasisInstall install, string? token = null, CancellationToken ct = default)
    {
        lock (_gate)
            if (_cache.TryGetValue(install.RepoRoot, out var cached) && DateTime.UtcNow - cached.At < Lifetime) return cached.Target;
        var unity = install.UnityVersion is { Length: > 0 } version && version != "unknown" ? version : UnityProjectService.ReadUnityVersion(install.UnityProjectPath);
        DateOnly? basisDate = null;
        string? basisBranch = null;
        if (install.IsGitRepo && _git.IsAvailable)
        {
            try
            {
                if (await _basis.ResolveBasisBaseAsync(install.RepoRoot, ct).ConfigureAwait(false) is { } basisBase)
                {
                    basisBranch = basisBase.Branch;
                    if (await CommitDateAsync(install.RepoRoot, basisBase.Sha, token, ct).ConfigureAwait(false) is { } when)
                        basisDate = DateOnly.FromDateTime(when.UtcDateTime);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { DiagnosticLog.Write($"Working out which Basis {install.RepoRoot} is on", ex); }
        }
        var target = ProjectTarget.From(unity, basisDate, basisBranch);
        lock (_gate) _cache[install.RepoRoot] = (target, DateTime.UtcNow);
        return target;
    }

    private async Task<DateTimeOffset?> CommitDateAsync(string repoRoot, string sha, string? token, CancellationToken ct)
    {
        lock (_gate)
            if (_commitDates.TryGetValue(sha, out var known)) return known;
        var when = await _git.GetCommitDateAsync(repoRoot, sha, ct).ConfigureAwait(false);
        if (when is null && UpmGitUrl.Parse(BasisInstallService.BasisRepoUrl) is { IsGitHub: true } basis)
            when = await _github.GetCommitDateAsync(basis.Owner, basis.Repo, sha, token, ct).ConfigureAwait(false);
        lock (_gate) _commitDates[sha] = when;
        return when;
    }
}
