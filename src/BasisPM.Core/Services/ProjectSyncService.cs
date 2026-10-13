using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record ProjectSyncResult(bool Ok, string Message)
{
    public bool Pulled { get; init; }
    public int Commits { get; init; }
    public BasisDevResult? Packages { get; init; }
}

public sealed class ProjectSyncService
{
    private readonly GitService _git;
    private readonly BasisDevService _basisDev;
    private readonly UnityProjectService _projects;

    public ProjectSyncService(GitService git, BasisDevService basisDev, UnityProjectService projects)
    {
        _git = git;
        _basisDev = basisDev;
        _projects = projects;
    }

    public async Task<ProjectSyncResult> SyncAsync(BasisInstall install, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return new ProjectSyncResult(false, "Git was not found on PATH.");
        var pulled = false;
        var commits = 0;
        string? pullNote = null;
        if (install.IsGitRepo)
        {
            if (await _git.GetOperationInProgressAsync(install.RepoRoot, ct).ConfigureAwait(false) is { } operation)
                return new ProjectSyncResult(false, $"The project is in the middle of a {operation}. Finish or abort it first.");
            var upstream = await _git.GetUpstreamNameAsync(install.RepoRoot, ct).ConfigureAwait(false);
            if (upstream is null) pullNote = "The current branch doesn't track a remote branch, so nothing was pulled.";
            else
            {
                onProgress?.Invoke($"Pulling from {upstream}…");
                var before = await _git.ResolveCommitAsync(install.RepoRoot, "HEAD", ct).ConfigureAwait(false);
                var pull = await _git.PullAsync(install.RepoRoot, onProgress, ct).ConfigureAwait(false);
                if (!pull.Ok) return new ProjectSyncResult(false, DescribePullFailure(upstream, pull.Output));
                var after = await _git.ResolveCommitAsync(install.RepoRoot, "HEAD", ct).ConfigureAwait(false);
                if (before is not null && after is not null && !string.Equals(before, after, StringComparison.OrdinalIgnoreCase))
                {
                    pulled = true;
                    commits = await _git.CountCommitsAsync(install.RepoRoot, $"{before}..{after}", ct).ConfigureAwait(false);
                }
            }
        }

        try { install.Manifest = (await _projects.LoadAsync(install.UnityProjectPath, ct).ConfigureAwait(false)).Manifest; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Text.Json.JsonException) { DiagnosticLog.Write($"Reloading {install.UnityProjectPath} after a pull", ex); }

        var packages = await _basisDev.RestoreAllAsync(install, switchListed: true, onProgress, ct).ConfigureAwait(false);
        var pullText = pulled ? $"Pulled {commits} commit{(commits == 1 ? "" : "s")}." : pullNote ?? "Already up to date with the remote.";
        return new ProjectSyncResult(packages.Ok, pullText + " " + packages.Message) { Pulled = pulled, Commits = commits, Packages = packages };
    }

    public static string DescribePullFailure(string upstream, string output)
    {
        if (output.Contains("Not possible to fast-forward", StringComparison.OrdinalIgnoreCase) || output.Contains("diverg", StringComparison.OrdinalIgnoreCase))
            return $"Your branch and {upstream} have both changed. Merge or rebase them in your git tool first, then sync again.";
        if (output.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase))
            return $"Your uncommitted edits touch files that changed on {upstream}. Commit or set them aside first, then sync again.";
        return $"Couldn't pull from {upstream}: {output}";
    }
}
