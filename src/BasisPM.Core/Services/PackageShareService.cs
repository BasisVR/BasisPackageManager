using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record PackageShareResult(bool Ok, string Message)
{
    public string? Url { get; init; }
    public string? Branch { get; init; }
    public string? Commit { get; init; }
    public bool Forked { get; init; }
}

public sealed class PackageShareService
{
    private static readonly Regex BranchChars = new("[^A-Za-z0-9._-]+", RegexOptions.CultureInvariant);

    private readonly GitService _git;
    private readonly GitHubApiService _api;
    private readonly MountService _mounts;
    private readonly MountRegistry _registry;

    public PackageShareService(GitService git, GitHubApiService api, MountService mounts, MountRegistry registry)
    {
        _git = git;
        _api = api;
        _mounts = mounts;
        _registry = registry;
    }

    public static string SuggestBranch(BasisInstall install) =>
        Slug(install.DisplayName) is { Length: > 0 } project ? project + "-changes" : "team-changes";

    public async Task<(UpmGitUrl? Original, bool CanShare, string? Reason)> DescribeAsync(BasisInstall install, string packageId, CancellationToken ct = default)
    {
        if (_mounts.FindMount(install.UnityProjectPath, packageId) is not { } mount || !MountService.IsWorkingClone(mount.FolderPath))
            return (null, false, $"{packageId} isn't installed as a working copy.");
        if (!BasisDevStore.IsCloneFolder(install.UnityProjectPath, mount.FolderPath))
            return (null, false, $"{packageId} isn't in {BasisDevStore.FolderName}, so teammates can't get it from Packages/{BasisPackagesStore.FileName}.");
        var original = OriginalOf(install, packageId, mount) ?? UpmGitUrl.Parse(await _git.GetConfigValueAsync(mount.FolderPath, "remote.origin.url", ct).ConfigureAwait(false));
        if (original is not { IsGitHub: true }) return (original, false, "Sharing needs the package to come from GitHub.");
        if (!await _mounts.HasLocalWorkAsync(mount.FolderPath, ct).ConfigureAwait(false)) return (original, false, "There's nothing to share: the clone has no edits or commits of its own.");
        return (original, true, null);
    }

    public async Task<PackageShareResult> ShareAsync(BasisInstall install, string packageId, string token, GitHubUser user, string? commitMessage, string? branch, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var project = install.UnityProjectPath;
        if (_mounts.FindMount(project, packageId) is not { } mount || !MountService.IsWorkingClone(mount.FolderPath))
            return new PackageShareResult(false, $"{packageId} isn't installed as a working copy.");
        var folder = mount.FolderPath;
        var sidecar = BasisDevStore.Read(project, packageId) ?? BasisPackagesStore.SidecarFor(project, packageId);
        var original = OriginalOf(install, packageId, mount) ?? UpmGitUrl.Parse(await _git.GetConfigValueAsync(folder, "remote.origin.url", ct).ConfigureAwait(false));
        if (original is not { IsGitHub: true }) return new PackageShareResult(false, "Sharing needs the package to come from GitHub.");
        if (await _git.GetOperationInProgressAsync(folder, ct).ConfigureAwait(false) is { } operation)
            return new PackageShareResult(false, $"The clone is in the middle of a {operation}. Finish or abort it first.");

        var current = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false);
        var target = Slug(string.IsNullOrWhiteSpace(branch) ? current is not null && !IsDefaultName(current) ? current : SuggestBranch(install) : branch);
        if (target.Length == 0 || !await _git.IsValidBranchNameAsync(folder, target, ct).ConfigureAwait(false))
            return new PackageShareResult(false, $"\"{branch}\" can't be used as a branch name.");
        if (!string.Equals(current, target, StringComparison.Ordinal))
        {
            var switched = await _git.CheckoutNewBranchAsync(folder, target, ct).ConfigureAwait(false);
            if (!switched.Ok && await _git.ResolveCommitAsync(folder, "refs/heads/" + target, ct).ConfigureAwait(false) is not null)
                return new PackageShareResult(false, $"A branch called {target} already exists in the clone. Pick another name.");
            if (!switched.Ok) return new PackageShareResult(false, $"Couldn't create branch {target}: {switched.Output}");
        }

        var changes = await _git.GetChangesAsync(folder, ct).ConfigureAwait(false);
        var upgrades = changes.Select(c => c.Path).Except(ApiUpgradeService.WithoutUpgrades(folder, changes).Select(c => c.Path), StringComparer.Ordinal).ToList();
        if (changes.Count > upgrades.Count)
        {
            if (string.IsNullOrWhiteSpace(commitMessage)) return new PackageShareResult(false, "Describe your changes so they can be committed.");
            onProgress?.Invoke("Committing your changes…");
            await _git.AddAllAsync(folder, ct).ConfigureAwait(false);
            if (upgrades.Count > 0) await _git.UnstagePathsAsync(folder, upgrades, ct).ConfigureAwait(false);
            var commit = await _git.CommitAsync(folder, commitMessage.Trim(), user.Name ?? user.Login, user.NoReplyEmail, ct).ConfigureAwait(false);
            if (!commit.Ok) return new PackageShareResult(false, $"Couldn't commit: {commit.Output}");
        }

        var repo = await _api.GetRepoAsync(token, original.Owner, original.Repo, ct).ConfigureAwait(false);
        if (repo is null) return new PackageShareResult(false, $"Couldn't look up {original.Slug} on GitHub. Check your connection and sign-in, then try again.");
        string owner = original.Owner, name = original.Repo;
        var forked = false;
        if (!repo.CanPush)
        {
            onProgress?.Invoke($"Forking {original.Slug}…");
            GitHubRepo fork;
            try { fork = await _api.ForkRepoAsync(token, original.Owner, original.Repo, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
            {
                DiagnosticLog.Write($"Forking {original.Slug}", ex);
                return new PackageShareResult(false, $"Couldn't fork {original.Slug}: {ex.Message}");
            }
            owner = fork.Owner?.Login ?? user.Login;
            name = fork.Name;
            forked = true;
            for (var attempt = 0; attempt < 10 && await _api.GetRepoAsync(token, owner, name, ct).ConfigureAwait(false) is null; attempt++)
            {
                onProgress?.Invoke("Waiting for the fork to be ready…");
                await Task.Delay(1500, ct).ConfigureAwait(false);
            }
        }

        onProgress?.Invoke($"Pushing {target} to {owner}/{name}…");
        var push = await _git.PushAsync(folder, $"https://x-access-token:{token}@github.com/{owner}/{name}.git", target, setUpstream: false, onProgress, ct).ConfigureAwait(false);
        if (!push.Ok) return new PackageShareResult(false, $"Couldn't push: {push.Output.Replace(token, "***", StringComparison.Ordinal)}");

        var head = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false) ?? "";
        var url = $"https://github.com/{owner}/{name}.git";
        var forkOf = forked ? original.CloneUrl : null;
        var subPath = sidecar?.Upstream.Path ?? UpmGitUrl.Parse(mount.OriginalManifestValue)?.Path;
        var originUrl = await _git.GetConfigValueAsync(folder, "remote.origin.url", ct).ConfigureAwait(false);
        if (forked && !PackageUpdateService.SameRepository(originUrl, url))
        {
            if (originUrl is not null && await _git.GetConfigValueAsync(folder, "remote.upstream.url", ct).ConfigureAwait(false) is null)
                await _git.SetRemoteAsync(folder, "upstream", originUrl, ct).ConfigureAwait(false);
            await _git.SetRemoteAsync(folder, "origin", url, ct).ConfigureAwait(false);
        }
        await _git.FetchWithTagsAsync(folder, "origin", null, null, ct).ConfigureAwait(false);

        var upstream = new BasisDevUpstream { Url = url, Ref = target, Path = subPath, Commit = head, ForkOf = forkOf };
        var manifestValue = UpmGitUrl.Parse(url)!.ToManifestUrl(target, subPath);
        var record = sidecar ?? new BasisDevSidecar { Package = packageId, Folder = BasisDevStore.RelativeFolder(project, folder) };
        record.Upstream = upstream;
        record.Manifest = new BasisDevManifestLine { Original = manifestValue, Mounted = record.Manifest?.Mounted ?? install.Manifest.Dependencies.GetValueOrDefault(packageId) };
        record.Recorded = DateTimeOffset.UtcNow;
        record.Tool = BasisDevStore.ToolName;
        BasisDevStore.Write(project, record);
        _registry.Add(mount with { OriginalManifestValue = manifestValue });
        MountService.ListClone(install, packageId, folder, upstream);
        return new PackageShareResult(true, $"Pushed {target} to {owner}/{name}. Packages/{BasisPackagesStore.FileName} now points teammates at it; they get it when they sync.")
        {
            Url = url,
            Branch = target,
            Commit = head,
            Forked = forked,
        };
    }

    private static UpmGitUrl? OriginalOf(BasisInstall install, string packageId, MountRecord mount)
    {
        var sidecar = BasisDevStore.Read(install.UnityProjectPath, packageId) ?? BasisPackagesStore.SidecarFor(install.UnityProjectPath, packageId);
        var recorded = sidecar?.Upstream.ForkOf is { Length: > 0 } forkOf ? forkOf : sidecar?.Upstream.Url;
        return UpmGitUrl.Parse(recorded) ?? UpmGitUrl.Parse(mount.OriginalManifestValue);
    }

    private static bool IsDefaultName(string branch) => branch is "main" or "master" or "developer" or "develop" or "HEAD";

    private static string Slug(string? text) => BranchChars.Replace((text ?? "").Trim().ToLowerInvariant().Replace(' ', '-'), "-").Trim('-', '.');
}
