using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Services;

public sealed class PublishHost : IPublishHost
{
    private readonly GitHubAuthService _auth;
    private readonly GitHubApiService _api;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<PackageIssue>>> _check;

    public PublishHost(GitHubAuthService auth, GitHubApiService api, Func<string, CancellationToken, Task<IReadOnlyList<PackageIssue>>> check)
    {
        _auth = auth;
        _api = api;
        _check = check;
    }

    public Task<string?> GetTokenAsync() => _auth.GetTokenAsync();

    public void RememberToken(string token) => _auth.SetPersonalAccessToken(token);

    public Task<GitHubUser?> GetUserAsync(string token) => _api.GetUserAsync(token);

    public Task<IReadOnlyList<PackageIssue>> CheckAsync(string packageRoot, CancellationToken ct) => _check(packageRoot, ct);
}
