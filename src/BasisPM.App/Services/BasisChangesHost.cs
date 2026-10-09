using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

public sealed class BasisChangesHost : IBasisChangesHost
{
    private readonly BasisContributeService _contribute;
    private readonly BasisUpdateService _updates;
    private readonly GitHubAuthService _auth;
    private readonly GitHubApiService _api;
    private readonly GitService _git;
    private readonly BasisInstall _install;

    public BasisChangesHost(BasisContributeService contribute, BasisUpdateService updates, GitHubAuthService auth, GitHubApiService api, GitService git, BasisInstall install)
    {
        _contribute = contribute;
        _updates = updates;
        _auth = auth;
        _api = api;
        _git = git;
        _install = install;
    }

    public string Repository => $"{_contribute.Owner}/{_contribute.Repo}";

    public Task<BasisContributeScan> ScanAsync(Action<string> progress, CancellationToken ct) =>
        Task.Run(() => _contribute.ScanAsync(_install.RepoRoot, _install.UnityProjectPath, progress, ct), ct);

    public async Task<string?> GetProjectRemoteAsync(string repoRoot)
    {
        var remotes = await _git.ListRemotesAsync(repoRoot);
        var url = remotes.Where(r => r.Name == "origin").Select(r => r.Url).FirstOrDefault() ?? remotes.Select(r => r.Url).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : url;
    }

    public Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(BasisContributeScan scan, BasisChange change, CancellationToken ct) =>
        Task.Run(() => _contribute.GetDiffAsync(scan, change, ct: ct), ct);

    public Task<IReadOnlyList<string>> ListBasisBranchesAsync() => _updates.ListBasisBranchesAsync();

    public Task<string?> GetTokenAsync() => _auth.GetTokenAsync();

    public void RememberToken(string token) => _auth.SetPersonalAccessToken(token);

    public Task<GitHubUser?> GetUserAsync(string token) => _api.GetUserAsync(token);

    public Task<BasisContributeResult> SubmitAsync(BasisContributeScan scan, IReadOnlyList<string> paths, BasisPullRequestDraft draft, string token, GitHubUser user, Action<string> progress, CancellationToken ct) =>
        Task.Run(() => _contribute.SubmitAsync(scan, paths, draft, token, user, progress, ct), ct);

    public void OpenUrl(string url) => ExternalLink.Open(url);
}
