using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

public sealed class SubmitPrHost : ISubmitPrHost
{
    private readonly GitService _git;
    private readonly GitHubAuthService _auth;
    private readonly GitHubApiService _api;
    private readonly string _folder;

    public SubmitPrHost(GitService git, GitHubAuthService auth, GitHubApiService api, string folder)
    {
        _git = git;
        _auth = auth;
        _api = api;
        _folder = folder;
    }

    public Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(GitFileChange change, CancellationToken ct) =>
        Task.Run(async () => BasisContributeService.ParseDiff(await _git.GetDiffAsync(_folder, change, ct)), ct);

    public Task<string?> GetTokenAsync() => _auth.GetTokenAsync();

    public void RememberToken(string token) => _auth.SetPersonalAccessToken(token);

    public Task<GitHubUser?> GetUserAsync(string token) => _api.GetUserAsync(token);
}
