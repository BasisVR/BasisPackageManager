using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record InstalledPackageSource(string PackageId, string? Folder, string Url, string? Ref, string? SubPath, string? Commit, string? ForkOf)
{
    public bool IsClone => Folder is not null;
    public string ReleaseUrl => string.IsNullOrWhiteSpace(ForkOf) ? Url : ForkOf!;
}

public sealed class PackageUpdateService
{
    private static readonly TimeSpan CheckLifetime = TimeSpan.FromMinutes(15);
    private static readonly Regex CommitSha = new("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant);
    private const int CompatibilityProbeLimit = 6;
    private const int CommitListLimit = 200;
    private const int ParallelChecks = 4;

    private readonly GitService _git;
    private readonly MountService _mounts;
    private readonly MountRegistry _registry;
    private readonly VersionService _versions;
    private readonly GitHubApiService _github;
    private readonly GitHubService _raw;
    private readonly Dictionary<string, (PackageUpdateInfo? Info, DateTime At)> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public PackageUpdateService(GitService git, MountService mounts, MountRegistry registry, VersionService? versions = null, GitHubApiService? github = null, GitHubService? raw = null)
    {
        _git = git;
        _mounts = mounts;
        _registry = registry;
        _github = github ?? new GitHubApiService();
        _versions = versions ?? new VersionService(_github, git);
        _raw = raw ?? new GitHubService();
    }

    public void ClearCache()
    {
        lock (_gate) _checks.Clear();
    }

    public void Forget(string packageId)
    {
        lock (_gate)
            foreach (var key in _checks.Keys.Where(k => k.StartsWith(packageId + "|", StringComparison.OrdinalIgnoreCase)).ToList()) _checks.Remove(key);
    }

    public async Task<InstalledPackageSource?> FindInstalledAsync(BasisInstall install, string packageId, CancellationToken ct = default)
    {
        var project = install.UnityProjectPath;
        if (_mounts.FindMount(project, packageId) is { } mount && MountService.IsWorkingClone(mount.FolderPath))
        {
            var sidecar = BasisDevStore.Read(project, packageId) ?? BasisPackagesStore.SidecarFor(project, packageId);
            var original = UpmGitUrl.Parse(mount.OriginalManifestValue);
            var url = sidecar?.Upstream.Url is { Length: > 0 } recorded ? recorded : original?.CloneUrl;
            url ??= UpmGitUrl.Parse(await _git.GetRemoteUrlAsync(mount.FolderPath, "origin", ct).ConfigureAwait(false))?.CloneUrl;
            if (string.IsNullOrWhiteSpace(url)) return null;
            var head = await _git.ResolveCommitAsync(mount.FolderPath, "HEAD", ct).ConfigureAwait(false);
            return new InstalledPackageSource(packageId, mount.FolderPath, url, sidecar is null ? original?.Ref : sidecar.Upstream.Ref, sidecar?.Upstream.Path ?? original?.Path, head, sidecar?.Upstream.ForkOf);
        }
        var line = ProjectManifestLine(install, packageId);
        if (UpmGitUrl.Parse(line) is not { } git) return null;
        return new InstalledPackageSource(packageId, null, git.CloneUrl, git.Ref, git.Path, null, null);
    }

    public async Task<IReadOnlyDictionary<string, PackageUpdateInfo>> CheckAllAsync(BasisInstall install, IEnumerable<string> packageIds, ProjectTarget target, Catalog? catalog, string? token, CancellationToken ct = default)
    {
        var results = new Dictionary<string, PackageUpdateInfo>(StringComparer.OrdinalIgnoreCase);
        using var slots = new SemaphoreSlim(ParallelChecks);
        var tasks = packageIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(async id =>
        {
            await slots.WaitAsync(ct).ConfigureAwait(false);
            try { return (Id: id, Info: await CheckAsync(install, id, target, catalog, token, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticLog.Write($"Checking {id} for updates", ex);
                return (Id: id, Info: (PackageUpdateInfo?)null);
            }
            finally { slots.Release(); }
        }).ToList();
        foreach (var (id, info) in await Task.WhenAll(tasks).ConfigureAwait(false))
            if (info is not null) results[id] = info;
        return results;
    }

    public async Task<PackageUpdateInfo?> CheckAsync(BasisInstall install, string packageId, ProjectTarget target, Catalog? catalog, string? token, CancellationToken ct = default)
    {
        var installed = await FindInstalledAsync(install, packageId, ct).ConfigureAwait(false);
        if (installed is null) return null;
        var key = string.Join('|', packageId, installed.ReleaseUrl, installed.Ref, installed.Commit, installed.SubPath, target.Unity, target.Basis, target.BasisBranch);
        lock (_gate)
            if (_checks.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < CheckLifetime) return cached.Info;
        var info = SemVer.TryParseTag(installed.Ref, out var current)
            ? await CheckReleasesAsync(installed, current, target, catalog, token, ct).ConfigureAwait(false)
            : await CheckBranchAsync(installed, ct).ConfigureAwait(false);
        lock (_gate) _checks[key] = (info, DateTime.UtcNow);
        return info;
    }

    private async Task<PackageUpdateInfo?> CheckReleasesAsync(InstalledPackageSource installed, SemVer current, ProjectTarget target, Catalog? catalog, string? token, CancellationToken ct)
    {
        var versions = await _versions.GetVersionsAsync(installed.ReleaseUrl, token, ct).ConfigureAwait(false);
        var candidates = new List<(PackageVersionOption Option, SemVer Version)>();
        foreach (var option in versions.Options)
        {
            if (option.Kind == VersionKind.Branch || !SemVer.TryParseTag(option.Ref, out var version) || version.CompareTo(current) <= 0) continue;
            if (current.PreRelease is null && (option.IsPrerelease || version.PreRelease is not null)) continue;
            candidates.Add((option, version));
        }
        candidates.Sort((a, b) => b.Version.CompareTo(a.Version));
        var none = new PackageUpdateInfo(installed.PackageId, installed.Url, installed.SubPath, PackageUpdateKind.Release, installed.Ref, installed.Commit, installed.Ref, null, false);
        if (candidates.Count == 0) return none;

        string? skippedRef = null;
        CompatibilityResult? skippedProblem = null;
        foreach (var (option, _) in candidates.Take(CompatibilityProbeLimit))
        {
            var problem = await CompatibilityOfAsync(installed, option.Ref!, target, catalog, ct).ConfigureAwait(false);
            if (problem.Ok)
                return new PackageUpdateInfo(installed.PackageId, installed.Url, installed.SubPath, PackageUpdateKind.Release, installed.Ref, installed.Commit, option.Ref, null, true) { SkippedRef = skippedRef, SkippedProblem = skippedProblem };
            if (skippedRef is null) (skippedRef, skippedProblem) = (option.Ref, problem);
        }
        return none with { SkippedRef = skippedRef, SkippedProblem = skippedProblem };
    }

    private async Task<PackageUpdateInfo?> CheckBranchAsync(InstalledPackageSource installed, CancellationToken ct)
    {
        if (installed.Ref is { } pinned && (CommitSha.IsMatch(pinned) || await IsTagOnRemoteAsync(installed.Url, pinned, ct).ConfigureAwait(false)))
            return new PackageUpdateInfo(installed.PackageId, installed.Url, installed.SubPath, PackageUpdateKind.Commits, pinned, installed.Commit, pinned, installed.Commit, false);
        var branch = installed.Ref ?? await _git.GetRemoteDefaultBranchAsync(installed.Url, ct).ConfigureAwait(false);
        if (branch is null) return null;
        var heads = await _git.ListRemoteHeadsAsync(installed.Url, ct).ConfigureAwait(false);
        if (!heads.TryGetValue(branch, out var tip)) return null;
        var available = installed.Commit is null || !string.Equals(tip, installed.Commit, StringComparison.OrdinalIgnoreCase);
        if (available && installed.Folder is not null && installed.Commit is not null && await _git.ResolveCommitAsync(installed.Folder, tip, ct).ConfigureAwait(false) is not null)
            available = !await _git.IsAncestorAsync(installed.Folder, tip, "HEAD", ct).ConfigureAwait(false);
        return new PackageUpdateInfo(installed.PackageId, installed.Url, installed.SubPath, PackageUpdateKind.Commits, installed.Ref, installed.Commit, installed.Ref, tip, available);
    }

    private async Task<bool> IsTagOnRemoteAsync(string url, string name, CancellationToken ct) =>
        (await _git.ListRemoteTagsAsync(url, ct).ConfigureAwait(false)).Contains(name, StringComparer.Ordinal);

    public async Task<CompatibilityResult> CompatibilityOfAsync(InstalledPackageSource installed, string gitRef, ProjectTarget target, Catalog? catalog, CancellationToken ct = default)
    {
        if (!target.IsKnown) return CompatibilityResult.Compatible;
        if (CatalogVersion(catalog, installed.PackageId, gitRef) is { } entry) return PackageCompatibility.Check(entry, target);
        if (installed.Folder is not null && await _git.ResolveCommitAsync(installed.Folder, "refs/tags/" + gitRef, ct).ConfigureAwait(false) is { } local)
        {
            var text = await _git.ShowFileAsync(installed.Folder, local, PackageJsonPath(installed.SubPath), ct).ConfigureAwait(false);
            if (PackageJsonFile.Parse(text) is { } fromClone) return PackageCompatibility.Check(fromClone, target);
        }
        if (UpmGitUrl.Parse(installed.ReleaseUrl) is { IsGitHub: true } github)
        {
            var package = await _raw.FetchPackageJsonAsync(new GitHubLocator(github.Owner, github.Repo, gitRef, installed.SubPath), ct).ConfigureAwait(false);
            if (package is not null) return PackageCompatibility.Check(package, target);
        }
        return CompatibilityResult.Compatible;
    }

    public Func<PackageUpdateTarget, CancellationToken, Task<bool>> ReviewBeforeUpdate(Func<PackageReview, Task<bool>> review) => async (target, ct) =>
    {
        var destination = Path.Combine(Path.GetTempPath(), "basispm-review", Guid.NewGuid().ToString("N"));
        try
        {
            var exported = await _git.ExportTreeAsync(target.Folder, target.Commit, null, destination, ct).ConfigureAwait(false);
            if (!exported.Ok)
            {
                DiagnosticLog.Write($"Exporting {target.PackageId} at {target.Commit} for review", new InvalidOperationException(exported.Output));
                return false;
            }
            var root = string.IsNullOrEmpty(target.Source.Path) ? destination : Path.Combine(destination, target.Source.Path.Replace('/', Path.DirectorySeparatorChar));
            return await review(new PackageReview(target.PackageId, destination, root, target.Source, target.Commit) { Package = PackageJsonFile.Read(root) }).ConfigureAwait(false);
        }
        finally
        {
            try { if (Directory.Exists(destination)) await BasisInstallService.DeleteFolderAsync(destination).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting the review copy {destination}", ex); }
        }
    };

    public static CatalogPackageVersion? CatalogVersion(Catalog? catalog, string packageId, string? gitRef)
    {
        if (catalog is null || string.IsNullOrWhiteSpace(gitRef)) return null;
        var package = catalog.Packages.FirstOrDefault(p => string.Equals(p.Key, packageId, StringComparison.OrdinalIgnoreCase)).Value;
        if (package?.Versions is null) return null;
        var byRef = package.Versions.Values.FirstOrDefault(v => string.Equals(v.Ref, gitRef, StringComparison.Ordinal));
        if (byRef is not null) return byRef;
        if (!SemVer.TryParseTag(gitRef, out var wanted)) return null;
        return package.Versions.FirstOrDefault(kv => SemVer.TryParse(kv.Key, out var v) && v.CompareTo(wanted) == 0 && v.PreRelease == wanted.PreRelease).Value;
    }

    public async Task<PackageChanges> GetChangesAsync(BasisInstall install, string packageId, string? toRef, string? toCommit, string? token, CancellationToken ct = default)
    {
        var installed = await FindInstalledAsync(install, packageId, ct).ConfigureAwait(false);
        if (installed is null) return PackageChanges.Empty;
        var releases = new List<PackageReleaseNote>();
        var source = UpmGitUrl.Parse(installed.ReleaseUrl);
        var hasFrom = SemVer.TryParseTag(installed.Ref, out var fromVersion);
        if (source is { IsGitHub: true } && SemVer.TryParseTag(toRef, out var toVersion))
        {
            foreach (var release in await _github.GetReleasesAsync(source.Owner, source.Repo, token, ct).ConfigureAwait(false))
            {
                if (release.Draft || !SemVer.TryParseTag(release.TagName, out var version) || version.CompareTo(toVersion) > 0 || (hasFrom && version.CompareTo(fromVersion) <= 0)) continue;
                releases.Add(new PackageReleaseNote(release.TagName, release.Name, release.Body, release.PublishedAt, release.HtmlUrl, release.Prerelease));
            }
            releases.Sort((a, b) => SemVer.TryParseTag(b.Tag, out var x) && SemVer.TryParseTag(a.Tag, out var y) ? x.CompareTo(y) : 0);
        }

        var commits = new List<GitCommitInfo>();
        var more = false;
        if (installed.Folder is not null && installed.Commit is not null && _git.IsAvailable)
        {
            var fetch = await _git.FetchWithTagsAsync(installed.Folder, "origin", null, null, ct).ConfigureAwait(false);
            if (!fetch.Ok) DiagnosticLog.Write($"Fetching {installed.Url} for the changes in {packageId}", new InvalidOperationException(fetch.Output));
            var target = toCommit is { Length: > 0 } ? await _git.ResolveCommitAsync(installed.Folder, toCommit, ct).ConfigureAwait(false) : null;
            if (target is null && toRef is { Length: > 0 })
                target = await _git.ResolveCommitAsync(installed.Folder, "refs/tags/" + toRef, ct).ConfigureAwait(false)
                    ?? await _git.ResolveCommitAsync(installed.Folder, "refs/remotes/origin/" + toRef, ct).ConfigureAwait(false);
            if (target is not null)
            {
                var log = await _git.GetLogAsync(installed.Folder, $"{installed.Commit}..{target}", CommitListLimit + 1, ct).ConfigureAwait(false);
                more = log.Count > CommitListLimit;
                commits.AddRange(log.Take(CommitListLimit));
            }
        }
        string? compare = null;
        if (source is { IsGitHub: true } && (installed.Ref ?? installed.Commit) is { Length: > 0 } from && (toRef ?? toCommit) is { Length: > 0 } to)
            compare = $"https://github.com/{source.Owner}/{source.Repo}/compare/{Uri.EscapeDataString(from)}...{Uri.EscapeDataString(to)}";
        return new PackageChanges(releases, commits, more, compare);
    }

    public async Task<PackageUpdateResult> UpdateCloneAsync(BasisInstall install, string packageId, PackageUpdateRequest request,
        Func<PackageUpdateTarget, CancellationToken, Task<bool>>? approve = null, Action<string>? onProgress = null, CancellationToken ct = default)
    {
        var (targetRef, targetUrl, targetCommit) = (request.Ref, request.Url, request.Commit);
        if (!_git.IsAvailable) return PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, "Git was not found on PATH.");
        if (!GitUrlPolicy.IsSafeRef(targetRef) || !GitUrlPolicy.IsSafeRef(targetCommit))
            return PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, "The version to update to isn't a valid git ref.");
        var project = install.UnityProjectPath;
        var mount = _mounts.FindMount(project, packageId);
        var folder = mount?.FolderPath;
        if (mount is null || folder is null || !MountService.IsWorkingClone(folder))
            return PackageUpdateResult.Fail(PackageUpdateOutcome.NotAClone, $"{packageId} isn't installed as a working copy, so it can't be updated in place.");

        var sidecar = BasisDevStore.Read(project, packageId) ?? BasisPackagesStore.SidecarFor(project, packageId);
        var original = UpmGitUrl.Parse(mount.OriginalManifestValue);
        var origin = await _git.GetConfigValueAsync(folder, "remote.origin.url", ct).ConfigureAwait(false);
        var recordedUrl = sidecar?.Upstream.Url is { Length: > 0 } recorded ? recorded : original?.CloneUrl ?? origin;
        var url = string.IsNullOrWhiteSpace(targetUrl) ? recordedUrl : targetUrl.Trim();
        if (string.IsNullOrWhiteSpace(url) || UpmGitUrl.Parse(url) is not { } source || !_git.CanFetch(source.CloneUrl))
            return PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"The upstream of {packageId} isn't recorded, so it can't be updated in place.");
        var subPath = sidecar?.Upstream.Path ?? original?.Path;
        var forkOf = request.ReplaceForkOf ? request.ForkOf : string.IsNullOrWhiteSpace(targetUrl) || SameRepository(targetUrl, recordedUrl) ? sidecar?.Upstream.ForkOf : null;
        var where = Relative(project, folder);
        if (await _git.GetOperationInProgressAsync(folder, ct).ConfigureAwait(false) is { } operation)
            return PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"{where} is in the middle of a {operation}. Finish or abort it there first.");

        string? previousOrigin = null;
        var addedUpstream = false;
        if (!SameRepository(origin, source.CloneUrl))
        {
            previousOrigin = origin;
            if (origin is not null && await _git.GetConfigValueAsync(folder, "remote.upstream.url", ct).ConfigureAwait(false) is null)
                addedUpstream = (await _git.SetRemoteAsync(folder, "upstream", origin, ct).ConfigureAwait(false)).Ok;
            var set = await _git.SetRemoteAsync(folder, "origin", source.CloneUrl, ct).ConfigureAwait(false);
            if (!set.Ok) return PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Couldn't point {where} at {source.Slug}: {set.Output}");
        }

        async Task<PackageUpdateResult> Abandon(PackageUpdateResult result)
        {
            if (previousOrigin is not null) await _git.SetRemoteAsync(folder, "origin", previousOrigin, CancellationToken.None).ConfigureAwait(false);
            if (addedUpstream) await _git.RemoveRemoteAsync(folder, "upstream", CancellationToken.None).ConfigureAwait(false);
            return result;
        }

        onProgress?.Invoke($"Fetching {source.Slug}…");
        var fetch = await _git.FetchWithTagsAsync(folder, "origin", null, onProgress, ct).ConfigureAwait(false);
        if (!fetch.Ok) return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Couldn't fetch {source.Slug}: {fetch.Output}")).ConfigureAwait(false);

        var resolvedRef = string.IsNullOrWhiteSpace(targetRef) ? null : targetRef.Trim();
        string? commit = null;
        if (!string.IsNullOrWhiteSpace(targetCommit))
        {
            commit = await _git.ResolveCommitAsync(folder, targetCommit.Trim(), ct).ConfigureAwait(false);
            if (commit is null && (await _git.FetchWithTagsAsync(folder, "origin", new[] { targetCommit.Trim() }, onProgress, ct).ConfigureAwait(false)).Ok)
                commit = await _git.ResolveCommitAsync(folder, targetCommit.Trim(), ct).ConfigureAwait(false);
            if (commit is null) return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Commit {PackageUpdateInfo.ShortCommit(targetCommit)} isn't on {source.Slug} any more.")).ConfigureAwait(false);
        }
        else if (resolvedRef is not null)
        {
            commit = await _git.ResolveCommitAsync(folder, "refs/tags/" + resolvedRef, ct).ConfigureAwait(false)
                ?? await _git.ResolveCommitAsync(folder, "refs/remotes/origin/" + resolvedRef, ct).ConfigureAwait(false)
                ?? (CommitSha.IsMatch(resolvedRef) ? await _git.ResolveCommitAsync(folder, resolvedRef, ct).ConfigureAwait(false) : null);
        }
        else
        {
            commit = await _git.ResolveCommitAsync(folder, "refs/remotes/origin/HEAD", ct).ConfigureAwait(false);
            if (commit is null && await _git.GetRemoteDefaultBranchAsync(source.CloneUrl, ct).ConfigureAwait(false) is { } defaultBranch)
                commit = await _git.ResolveCommitAsync(folder, "refs/remotes/origin/" + defaultBranch, ct).ConfigureAwait(false);
        }
        if (commit is null) return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"{source.Slug} has no {(resolvedRef is null ? "default branch" : resolvedRef)}.")).ConfigureAwait(false);
        if (await _git.ShowFileAsync(folder, commit, PackageJsonPath(subPath), ct).ConfigureAwait(false) is null)
            return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.NotAPackage, $"{source.Slug} has no {PackageJsonPath(subPath)} at {resolvedRef ?? PackageUpdateInfo.ShortCommit(commit)}, so Unity couldn't load it as a package.")).ConfigureAwait(false);

        var current = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
        var label = resolvedRef ?? PackageUpdateInfo.ShortCommit(commit);
        var manifestValue = source.ToManifestUrl(resolvedRef, subPath);
        if (current is not null && string.Equals(current, commit, StringComparison.OrdinalIgnoreCase))
        {
            Record(install, packageId, folder, mount, sidecar, source.CloneUrl, resolvedRef, subPath, commit, manifestValue, forkOf);
            Forget(packageId);
            return new PackageUpdateResult(PackageUpdateOutcome.UpToDate, $"{packageId} is already at {label}.") { FromCommit = current, ToCommit = commit, Ref = resolvedRef, ManifestValue = manifestValue };
        }

        if (approve is not null)
        {
            var review = new PackageUpdateTarget(packageId, folder, BasisDevStore.PackageRoot(folder, subPath), source with { Ref = resolvedRef, Path = subPath }, commit, resolvedRef);
            if (!await approve(review, ct).ConfigureAwait(false))
                return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Declined, $"{packageId} wasn't updated.")).ConfigureAwait(false);
        }

        if (current is not null)
        {
            var changed = await _git.GetChangedPathsAsync(folder, current, commit, ct).ConfigureAwait(false);
            if (await Task.Run(() => MountService.WriteBlocker(project, folder, changed), ct).ConfigureAwait(false) is { } blocker)
                return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Blocked, $"Couldn't update {packageId}: {blocker}")).ConfigureAwait(false);
        }

        await Task.Run(() => ApiUpgradeService.RevertAll(folder), ct).ConfigureAwait(false);
        var branch = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false);
        var status = await _git.GetStatusAsync(folder, ct).ConfigureAwait(false);
        var localCommits = current is null ? 0 : await _git.CountLocalCommitsAsync(folder, "HEAD", ct).ConfigureAwait(false);
        string? stash = null;
        if (status.ChangeCount > 0)
        {
            onProgress?.Invoke("Setting your edits aside…");
            var (stashed, sha) = await _git.StashEverythingAsync(folder, $"BasisPM: before updating {packageId} to {label}", ct).ConfigureAwait(false);
            if (!stashed.Ok) return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Couldn't set your edits in {where} aside: {stashed.Output}")).ConfigureAwait(false);
            stash = sha;
        }

        var merged = false;
        GitResult move;
        var fastForward = current is null || await _git.IsAncestorAsync(folder, current, commit, ct).ConfigureAwait(false);
        if (!fastForward && localCommits != 0)
        {
            onProgress?.Invoke($"Merging {label} into your commits…");
            move = await _git.MergeAsync(folder, commit, $"Merge {label} into local changes", fastForwardOnly: false, onProgress, ct).ConfigureAwait(false);
            if (!move.Ok)
            {
                var conflicts = (await _git.GetConflictsAsync(folder, CancellationToken.None).ConfigureAwait(false)).Select(c => c.Path).ToList();
                if (await _git.IsMergeInProgressAsync(folder, CancellationToken.None).ConfigureAwait(false)) await _git.AbortMergeAsync(folder, CancellationToken.None).ConfigureAwait(false);
                var restored = await RestoreAsync(folder, current, branch, stash).ConfigureAwait(false);
                return await Abandon(conflicts.Count > 0
                    ? new PackageUpdateResult(PackageUpdateOutcome.Conflict, $"Your commits in {where} change the same lines as {label} in {Plural(conflicts.Count, "file")}, so {packageId} was left as it was.{restored}") { ConflictedFiles = conflicts, Ref = resolvedRef }
                    : PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Couldn't merge {label} into {where}: {move.Output}{restored}")).ConfigureAwait(false);
            }
            merged = true;
        }
        else
        {
            onProgress?.Invoke($"Checking out {label}…");
            var tracking = branch is not null && (string.Equals(branch, resolvedRef, StringComparison.Ordinal) || resolvedRef is null && await IsRemoteBranchAsync(folder, branch, commit, ct).ConfigureAwait(false));
            move = tracking && fastForward && current is not null
                ? await _git.MergeAsync(folder, commit, "", fastForwardOnly: true, onProgress, ct).ConfigureAwait(false)
                : await _git.CheckoutDetachedAsync(folder, commit, ct).ConfigureAwait(false);
            if (!move.Ok)
            {
                var restored = await RestoreAsync(folder, current, branch, stash).ConfigureAwait(false);
                return await Abandon(PackageUpdateResult.Fail(PackageUpdateOutcome.Failed, $"Couldn't check out {label} in {where}: {move.Output}{restored}")).ConfigureAwait(false);
            }
        }

        if (stash is not null)
        {
            onProgress?.Invoke("Putting your edits back…");
            var apply = await _git.ApplyStashAsync(folder, stash, ct).ConfigureAwait(false);
            if (!apply.Ok)
            {
                var conflicts = (await _git.GetConflictsAsync(folder, CancellationToken.None).ConfigureAwait(false)).Select(c => c.Path).ToList();
                var restored = await RestoreAsync(folder, current, branch, stash).ConfigureAwait(false);
                return await Abandon(new PackageUpdateResult(PackageUpdateOutcome.Conflict,
                    $"Your edits in {where} change the same lines as {label}{(conflicts.Count > 0 ? $" in {Plural(conflicts.Count, "file")}" : "")}, so {packageId} was left as it was.{restored}") { ConflictedFiles = conflicts, Ref = resolvedRef }).ConfigureAwait(false);
            }
            await _git.DropStashAsync(folder, stash, CancellationToken.None).ConfigureAwait(false);
        }

        Record(install, packageId, folder, mount, sidecar, source.CloneUrl, resolvedRef, subPath, commit, manifestValue, forkOf);
        Forget(packageId);
        var kept = stash is not null || merged;
        return new PackageUpdateResult(PackageUpdateOutcome.Updated, $"Updated {packageId} to {label}{(kept ? ", keeping your changes" : "")}.")
        {
            FromCommit = current,
            ToCommit = await _git.ResolveCommitAsync(folder, "HEAD", CancellationToken.None).ConfigureAwait(false) ?? commit,
            Ref = resolvedRef,
            ManifestValue = manifestValue,
            KeptLocalChanges = kept,
            Merged = merged,
        };
    }

    private async Task<bool> IsRemoteBranchAsync(string folder, string branch, string commit, CancellationToken ct) =>
        string.Equals(await _git.ResolveCommitAsync(folder, "refs/remotes/origin/" + branch, ct).ConfigureAwait(false), commit, StringComparison.OrdinalIgnoreCase);

    private async Task<string> RestoreAsync(string folder, string? commit, string? branch, string? stash)
    {
        var ct = CancellationToken.None;
        if (branch is not null)
        {
            await _git.ForceCheckoutAsync(folder, branch, detach: false, ct).ConfigureAwait(false);
            if (commit is not null) await _git.ResetHardAsync(folder, commit, ct).ConfigureAwait(false);
        }
        else if (commit is not null) await _git.ForceCheckoutAsync(folder, commit, detach: true, ct).ConfigureAwait(false);
        if (stash is null) return "";
        await _git.CleanUntrackedAsync(folder, ct).ConfigureAwait(false);
        var apply = await _git.ApplyStashAsync(folder, stash, ct).ConfigureAwait(false);
        if (apply.Ok)
        {
            await _git.DropStashAsync(folder, stash, ct).ConfigureAwait(false);
            return "";
        }
        DiagnosticLog.Write($"Putting the edits in {folder} back", new InvalidOperationException(apply.Output));
        return $" Your edits are saved in git stash {PackageUpdateInfo.ShortCommit(stash)}; run 'git stash apply {stash}' in that folder to get them back.";
    }

    private void Record(BasisInstall install, string packageId, string folder, MountRecord mount, BasisDevSidecar? sidecar, string url, string? gitRef, string? subPath, string commit, string manifestValue, string? forkOf)
    {
        var project = install.UnityProjectPath;
        try
        {
            var upstream = new BasisDevUpstream { Url = url, Ref = gitRef, Path = subPath, Commit = commit, ForkOf = forkOf };
            var mounted = sidecar?.Manifest.Mounted ?? (BasisDevStore.PackageRoot(folder, subPath) is { } root ? BasisDevStore.ManifestLine(project, root) : null);
            var record = sidecar ?? new BasisDevSidecar { Package = packageId, Folder = BasisDevStore.RelativeFolder(project, folder), Tool = BasisDevStore.ToolName };
            record.Upstream = upstream;
            record.Manifest = new BasisDevManifestLine { Original = manifestValue, Mounted = mounted };
            record.Recorded = DateTimeOffset.UtcNow;
            record.Tool = BasisDevStore.ToolName;
            if (BasisDevStore.ResolveFolder(project, record.Folder) is not null) BasisDevStore.Write(project, record);
            _registry.Add(mount with { OriginalManifestValue = manifestValue });
            MountService.ListClone(install, packageId, folder, upstream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException)
        {
            DiagnosticLog.Write($"Recording the update of {packageId}", ex);
        }
    }

    private static string? ProjectManifestLine(BasisInstall install, string packageId)
    {
        var key = install.Manifest.Dependencies.Keys.FirstOrDefault(k => string.Equals(k, packageId, StringComparison.OrdinalIgnoreCase));
        return key is null ? null : install.Manifest.Dependencies[key];
    }

    public static string PackageJsonPath(string? subPath) => string.IsNullOrEmpty(subPath) ? PackageJsonFile.FileName : subPath.Trim('/') + "/" + PackageJsonFile.FileName;

    public static bool SameRepository(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var left = UpmGitUrl.Parse(a)?.CloneUrl ?? a.Trim();
        var right = UpmGitUrl.Parse(b)?.CloneUrl ?? b.Trim();
        static string Normalize(string url) => url.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? url.TrimEnd('/')[..^4] : url.TrimEnd('/');
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Relative(string project, string folder)
    {
        var relative = Path.GetRelativePath(project, folder).Replace('\\', '/');
        return relative.StartsWith("..", StringComparison.Ordinal) ? folder : relative;
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
