using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record PrRequest(string Title, string? Body, string Branch, string? CommitMessage);

public sealed record ContributeResult(bool Ok, string? PrUrl, bool Forked, string? Error)
{
    public static ContributeResult Success(string prUrl, bool forked) => new(true, prUrl, forked, null);
    public static ContributeResult Fail(string error) => new(false, null, false, error);
}

/// <summary>
/// Turns a locally-mounted, edited package into a pull request: create a branch, commit, push to the
/// upstream repo (or a fork when the user lacks push access), then open the PR. GitHub only for now.
/// </summary>
public sealed class ContributeService
{
    private readonly GitService _git;
    private readonly GitHubApiService _api;

    public ContributeService(GitService git, GitHubApiService api)
    {
        _git = git;
        _api = api;
    }

    public async Task<ContributeResult> SubmitPrAsync(string folderPath, string token, GitHubUser user, PrRequest pr, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var origin = await _git.GetRemoteUrlAsync(folderPath, "origin", ct).ConfigureAwait(false);
        var upstream = UpmGitUrl.Parse(origin);
        if (upstream is null || !upstream.IsGitHub)
            return ContributeResult.Fail("The mounted package's origin isn't a GitHub repo — pull requests are GitHub-only right now.");

        var repo = await _api.GetRepoAsync(token, upstream.Owner, upstream.Repo, ct).ConfigureAwait(false);
        var baseBranch = repo?.DefaultBranch is { Length: > 0 } db ? db : "main";

        onProgress?.Invoke($"Creating branch {pr.Branch}…");
        var branch = await _git.CheckoutNewBranchAsync(folderPath, pr.Branch, ct).ConfigureAwait(false);
        if (!branch.Ok) return ContributeResult.Fail($"Couldn't create branch '{pr.Branch}': {branch.Output}");

        await _git.AddAllAsync(folderPath, ct).ConfigureAwait(false);
        onProgress?.Invoke("Committing…");
        var commit = await _git.CommitAsync(folderPath, pr.CommitMessage ?? pr.Title, user.Name ?? user.Login, user.NoReplyEmail, ct).ConfigureAwait(false);
        if (!commit.Ok && !commit.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
            return ContributeResult.Fail($"Commit failed: {commit.Output}");

        // Push to the upstream if we can, otherwise fork and push there.
        string pushOwner, pushRepo, head;
        var forked = false;
        if (repo?.CanPush == true)
        {
            pushOwner = upstream.Owner;
            pushRepo = upstream.Repo;
            head = pr.Branch;
        }
        else
        {
            onProgress?.Invoke($"Forking {upstream.Owner}/{upstream.Repo}…");
            var fork = await _api.ForkRepoAsync(token, upstream.Owner, upstream.Repo, ct).ConfigureAwait(false);
            pushOwner = fork.Owner?.Login ?? user.Login;
            pushRepo = fork.Name;
            head = $"{pushOwner}:{pr.Branch}";
            forked = true;
            await WaitForForkAsync(token, pushOwner, pushRepo, onProgress, ct).ConfigureAwait(false);
        }

        var pushUrl = $"https://x-access-token:{token}@github.com/{pushOwner}/{pushRepo}.git";
        onProgress?.Invoke("Pushing…");
        var push = await _git.PushAsync(folderPath, pushUrl, pr.Branch, setUpstream: false, onProgress, ct).ConfigureAwait(false);
        if (!push.Ok) return ContributeResult.Fail($"Push failed: {Scrub(push.Output, token)}");

        onProgress?.Invoke("Opening the pull request…");
        try
        {
            var result = await _api.CreatePullRequestAsync(token, upstream.Owner, upstream.Repo, pr.Title, head, baseBranch, pr.Body, ct).ConfigureAwait(false);
            return ContributeResult.Success(result.HtmlUrl, forked);
        }
        catch (Exception ex)
        {
            return ContributeResult.Fail($"Pushed successfully, but opening the PR failed: {ex.Message}");
        }
    }

    // A freshly-created fork isn't immediately pushable; poll briefly until it exists.
    private async Task WaitForForkAsync(string token, string owner, string repo, Action<string>? onProgress, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await _api.GetRepoAsync(token, owner, repo, ct).ConfigureAwait(false) is not null) return;
            onProgress?.Invoke("Waiting for the fork to be ready…");
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    private static string Scrub(string text, string token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");
}
