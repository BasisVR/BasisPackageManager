using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const int DependencyDepth = 5;
    private PackageUpdateService? _packageUpdateService;
    private ProjectTargetService? _projectTargets;

    private PackageUpdateService PackageUpdates => _packageUpdateService ??= new PackageUpdateService(_git, _mounts, _mountRegistry, _versions, _ghApi, _github);
    private ProjectTargetService ProjectTargets => _projectTargets ??= new ProjectTargetService(_git, _updates, _ghApi);

    private async Task UpdatePackagesAsync(List<string> values)
    {
        var options = values.ToList();
        var dryRun = TakeFlag(options, "--dry-run");
        var notes = TakeFlag(options, "--notes");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var shipped = await LoadShippedPackagesAsync(install);
        var lines = shipped.Known
            ? install.Manifest.Dependencies.Where(d => UpmGitUrl.Parse(d.Value) is not null).Select(d => d.Key)
            : Enumerable.Empty<string>();
        var ids = options.Count > 0
            ? options
            : lines.Union(shipped.Mounted, StringComparer.OrdinalIgnoreCase).Where(id => !shipped.IsIncluded(id) && !shipped.IsBuiltIn(id, null)).ToList();
        if (options.Count == 0 && !shipped.Known)
            Out.Warning("This project's git history can't show which manifest.json lines belong to Basis, so only packages installed as working copies are checked. Name others to update them.");
        foreach (var id in ids.Where(shipped.IsIncluded).ToList())
        {
            Out.Warning($"{id} ships with Basis, so it's updated with Basis itself.");
            ids.Remove(id);
        }
        var token = await TryGitHubTokenAsync();
        IReadOnlyDictionary<string, PackageUpdateInfo> infos = new Dictionary<string, PackageUpdateInfo>();
        ProjectTarget target = ProjectTarget.Unknown;
        await RunOperationAsync(async ct =>
        {
            target = await ProjectTargets.ResolveAsync(install, token, ct);
            infos = await PackageUpdates.CheckAllAsync(install, ids, target, catalog, token, ct);
        }, "Looking for newer releases" + Out.Ellipsis);

        var plan = ids.Select(id => infos.GetValueOrDefault(id)).OfType<PackageUpdateInfo>().Where(i => i.Available).ToList();
        foreach (var info in infos.Values.Where(i => i.SkippedRef is not null && i.SkippedProblem is not null))
            Out.Hint($"{info.PackageId}: {info.SkippedRef} is out but {info.SkippedProblem!.Describe()}, so it isn't offered.");
        foreach (var id in ids.Where(id => !infos.ContainsKey(id)))
            Out.Warning($"{id} wasn't checked: it isn't installed from git, or its repository couldn't be reached.");
        if (Out.JsonMode)
        {
            Out.Json(plan.Select(i => new { id = i.PackageId, kind = i.Kind.ToString().ToLowerInvariant(), from = i.InstalledRef ?? PackageUpdateInfo.ShortCommit(i.InstalledCommit), to = TargetLabel(i), skipped = i.SkippedRef }));
            if (dryRun) return;
        }
        if (plan.Count == 0)
        {
            Out.Success(ids.Count == 0 ? "There are no git packages to update in this project." : "Every package is on its newest release.");
            return;
        }
        if (!Out.JsonMode)
        {
            Out.Heading(dryRun ? "Would update" : "Updates");
            Out.Table(plan.Select(i => new[] { new Span(i.PackageId), new Span(i.InstalledRef ?? PackageUpdateInfo.ShortCommit(i.InstalledCommit), Tone.Dim), new Span(Out.Arrow, Tone.Dim), new Span(TargetLabel(i), Tone.Good) }).ToList());
        }
        if (notes)
            foreach (var info in plan) await PrintChangesAsync(install, info.PackageId, info.TargetRef, info.TargetCommit, token, 12);
        if (dryRun) return;
        if (!assumeYes && !Confirm($"Update {Plural(plan.Count, "package")}?"))
        {
            Out.Warning("Cancelled.");
            return;
        }
        var failed = 0;
        foreach (var info in plan)
        {
            try
            {
                if (!await UpdateOnePackageAsync(install, catalog, info, assumeYes, shipped)) failed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                DiagnosticLog.Write($"Updating {info.PackageId}", ex);
                Out.Error($"{info.PackageId}: {ex.Message}");
                failed++;
            }
        }
        if (failed > 0) _exitCode = 1;
    }

    private async Task<bool> UpdateOnePackageAsync(BasisInstall install, Catalog catalog, PackageUpdateInfo info, bool assumeYes, ShippedPackages shipped)
    {
        var entry = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(info.PackageId, StringComparison.OrdinalIgnoreCase));
        if (WorkingCloneMount(install, info.PackageId) is null)
        {
            if (entry is not null) return await InstallCatalogPackageAsync(install, catalog, entry, info.TargetRef ?? "default", assumeYes, shipped);
            var url = UpmGitUrl.Parse(info.Url)?.ToManifestUrl(info.TargetRef, info.SubPath) ?? info.Url;
            await CloneInstallAsync(install, info.PackageId, url, false, ReviewGate($"{info.PackageId} isn't in the Basis registry, so nobody has checked it for you.", null, false, assumeYes), reviewRequired: true, assumeYes: assumeYes);
            Out.Success($"Updated {info.PackageId} to {TargetLabel(info)}.");
            return true;
        }
        var registry = entry is not null && !_unofficialIds.Contains(entry.Name);
        var review = ReviewGate($"{info.PackageId} {(registry ? "has a version the Basis registry hasn't checked" : "isn't in the Basis registry")}, so nobody has checked it for you.", entry, registry, assumeYes);
        var request = new PackageUpdateRequest(info.TargetRef, Commit: info.Kind == PackageUpdateKind.Commits ? info.TargetCommit : null);
        PackageUpdateResult result = null!;
        await RunOperationAsync(async ct => result = await PackageUpdates.UpdateCloneAsync(install, info.PackageId, request, PackageUpdates.ReviewBeforeUpdate(review), Out.Progress, ct), $"Updating {info.PackageId}" + Out.Ellipsis);
        switch (result.Outcome)
        {
            case PackageUpdateOutcome.Updated or PackageUpdateOutcome.UpToDate:
                Out.Success(result.Message);
                if (entry is not null && await SyncServerSideAsync(install, entry, result.ManifestValue ?? info.Url) is { } problem)
                    Out.Warning($"The Unity side is updated, but the Basis Server side was not: {problem}");
                await InstallMissingDependenciesAsync(install, catalog, info.PackageId, assumeYes, shipped, 0);
                return true;
            case PackageUpdateOutcome.Declined:
                Out.Warning($"Left {info.PackageId} as it was.");
                return false;
            case PackageUpdateOutcome.Conflict:
                Out.Error(result.Message);
                foreach (var file in result.ConflictedFiles.Take(10)) Out.Say(new Span("  " + file, Tone.Dim));
                Out.Hint($"Commit or set your edits aside, or replace your copy with '{Prefix}install-package {info.PackageId} --version {info.TargetRef ?? "default"} --yes'.");
                return false;
            default:
                Out.Error(result.Message);
                return false;
        }
    }

    private static string TargetLabel(PackageUpdateInfo info) =>
        info.Kind == PackageUpdateKind.Release ? info.TargetRef ?? "" : $"{info.TargetRef ?? "default branch"} @ {PackageUpdateInfo.ShortCommit(info.TargetCommit)}";

    private async Task ReleaseNotesAsync(List<string> values)
    {
        var options = values.ToList();
        var to = TakeValue(options, "--to");
        Expect(options, 1, 1, "a package id");
        var id = options[0];
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var token = await TryGitHubTokenAsync();
        PackageUpdateInfo? info = null;
        if (to is null)
        {
            await RunOperationAsync(async ct => info = await PackageUpdates.CheckAsync(install, id, await ProjectTargets.ResolveAsync(install, token, ct), catalog, token, ct), "Looking for newer releases" + Out.Ellipsis);
            if (info is null) throw new InvalidOperationException($"{id} isn't installed from git in {install.DisplayName}, so there's nothing to compare.");
            if (!info.Available)
            {
                Out.Success($"{id} is on its newest release ({info.InstalledRef ?? PackageUpdateInfo.ShortCommit(info.InstalledCommit)}). Pass --to <ref> to see the changes up to another version.");
                return;
            }
        }
        else if (!GitUrlPolicy.IsSafeRef(to)) throw new UsageException($"'{to}' isn't a valid git ref.");
        await PrintChangesAsync(install, id, to ?? info!.TargetRef, to is null ? info!.TargetCommit : null, token, 0);
    }

    private async Task PrintChangesAsync(BasisInstall install, string id, string? toRef, string? toCommit, string? token, int bodyLines)
    {
        PackageChanges changes = PackageChanges.Empty;
        await RunOperationAsync(async ct => changes = await PackageUpdates.GetChangesAsync(install, id, toRef, toCommit, token, ct), $"Reading the changes in {id}" + Out.Ellipsis);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                id,
                to = toRef ?? toCommit,
                releases = changes.Releases.Select(r => new { tag = r.Tag, name = r.Name, published = r.Published, url = r.Url, prerelease = r.Prerelease, notes = r.Body }),
                commits = changes.Commits.Select(c => new { sha = c.Sha, author = c.Author, date = c.Date, subject = c.Subject }),
                moreCommits = changes.MoreCommits,
                compare = changes.CompareUrl,
            });
            return;
        }
        Out.Heading($"{id} up to {toRef ?? PackageUpdateInfo.ShortCommit(toCommit)}");
        if (changes.IsEmpty) Out.Say(new Span("No release notes or commits are available.", Tone.Dim));
        foreach (var release in changes.Releases)
        {
            Out.Say(new Span(release.Tag, Tone.Bold), new Span(string.IsNullOrWhiteSpace(release.Name) || release.Name == release.Tag ? "" : "  " + release.Name), new Span(release.Published is { } when ? "  " + when.ToString("yyyy-MM-dd") : "", Tone.Dim));
            var body = (release.Body ?? "").Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
            foreach (var line in bodyLines > 0 ? body.Take(bodyLines) : body)
                foreach (var wrapped in Out.Wrap(line, Math.Max(40, Out.Width - 4))) Out.Say("  " + wrapped);
            if (bodyLines > 0 && body.Count > bodyLines) Out.Say(new Span($"  ({body.Count - bodyLines} more lines; '{Prefix}release-notes {id}' shows them all)", Tone.Dim));
        }
        if (changes.Commits.Count > 0)
        {
            Out.Heading("Commits");
            Out.Table(changes.Commits.Select(c => new[] { new Span(c.ShortSha, Tone.Dim), new Span(Out.Piped ? c.Subject : Out.Truncate(c.Subject, 70)), new Span(c.Author, Tone.Dim) }).ToList());
            if (changes.MoreCommits) Out.Hint("Only the newest 200 commits are shown.");
        }
        if (changes.CompareUrl is { } compare) Out.Hint("Full comparison: " + compare);
    }

    private async Task RestorePackagesAsync(List<string> values)
    {
        var options = values.ToList();
        var noSwitch = TakeFlag(options, "--no-switch");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 0, 0, "");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        BasisDevResult result = null!;
        _basisDev.ApproveUpdate = ListedReview(catalog, assumeYes);
        try { await RunOperationAsync(async ct => result = await _basisDev.RestoreAllAsync(install, !noSwitch, Out.Progress, ct), "Restoring the listed packages" + Out.Ellipsis); }
        finally { _basisDev.ApproveUpdate = null; }
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Out.Success(result.Message);
    }

    private async Task SyncProjectAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 0, 0, "");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        ProjectSyncResult result = null!;
        _basisDev.ApproveUpdate = ListedReview(catalog, assumeYes);
        try { await RunOperationAsync(async ct => result = await new ProjectSyncService(_git, _basisDev, _projects).SyncAsync(install, Out.Progress, ct), "Syncing" + Out.Ellipsis); }
        finally { _basisDev.ApproveUpdate = null; }
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Out.Success(result.Message);
    }

    private Func<PackageUpdateTarget, CancellationToken, Task<bool>> ListedReview(Catalog catalog, bool assumeYes) => (target, ct) =>
    {
        var entry = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(target.PackageId, StringComparison.OrdinalIgnoreCase));
        var registry = entry is not null && !_unofficialIds.Contains(entry.Name) && PackageUpdateService.SameRepository(entry.Url, target.Source.CloneUrl);
        var review = ReviewGate($"{target.PackageId} comes from {target.Source.Slug}, which Packages/basispackages.json lists but the Basis registry hasn't checked.", registry ? entry : null, registry, assumeYes);
        return PackageUpdates.ReviewBeforeUpdate(review)(target, ct);
    };

    private async Task SharePackageAsync(BasisInstall install, List<string> options)
    {
        var branch = TakeValue(options, "--branch");
        var message = TakeValue(options, "--message");
        Expect(options, 2, 2, "a package id");
        var id = options[1];
        var shares = new PackageShareService(_git, _ghApi, _mounts, _mountRegistry);
        var (_, canShare, reason) = await shares.DescribeAsync(install, id);
        if (!canShare) throw new InvalidOperationException(reason ?? $"{id} can't be shared.");
        var token = await TryGitHubTokenAsync() ?? throw new InvalidOperationException("Sharing pushes to GitHub. Sign in with 'gh auth login', or set GH_TOKEN, then try again.");
        var user = await _ghApi.GetUserAsync(token) ?? throw new InvalidOperationException("GitHub didn't accept that token. Sign in again with 'gh auth login'.");
        if (message is null && WorkingCloneMount(install, id) is { } mount && (await _git.GetStatusAsync(mount.FolderPath)).ChangeCount > 0)
        {
            if (Console.IsInputRedirected) throw new UsageException("The clone has uncommitted edits. Describe them with --message \"...\".");
            Out.Prompt("Describe your changes: ", Tone.Accent);
            message = Console.ReadLine();
        }
        PackageShareResult result = null!;
        await RunOperationAsync(async ct => result = await shares.ShareAsync(install, id, token, user, message, branch, Out.Progress, ct), $"Sharing {id}" + Out.Ellipsis);
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Out.Success(result.Message);
        Out.Hint($"Commit Packages/{BasisPackagesStore.FileName} so teammates get it; they run '{Prefix}sync' or '{Prefix}restore'.");
    }

    private async Task InstallMissingDependenciesAsync(BasisInstall install, Catalog catalog, string packageId, bool assumeYes, ShippedPackages shipped, int depth)
    {
        if (depth >= DependencyDepth) return;
        var root = MountedPackageRoot(install, packageId)
            ?? UnityPackageMap.List(install.UnityProjectPath, includeAssets: false).FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Root;
        if (root is null) return;
        await ReloadManifestAsync(install);
        var gaps = PackageDependencies.AnalyzeInstalled(install.UnityProjectPath, root, install.Manifest, catalog);
        foreach (var dependency in gaps.Where(d => d.State == DependencyState.Missing))
            Out.Warning($"{packageId} needs {dependency.Id}, which isn't in the package registry or the project. Add it with '{Prefix}add-git <url>'.");
        foreach (var dependency in gaps.Where(d => d.CanInstall && !shipped.IsIncluded(d.Id)))
        {
            try
            {
                if (dependency.State == DependencyState.InCatalog && _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(dependency.Id, StringComparison.OrdinalIgnoreCase)) is { } entry)
                {
                    Out.Say(new Span($"{packageId} needs {dependency.Id}; installing it.", Tone.Dim));
                    if (await InstallCatalogPackageAsync(install, catalog, entry, null, assumeYes, shipped))
                        await InstallMissingDependenciesAsync(install, catalog, entry.Name, assumeYes, shipped, depth + 1);
                }
                else if (dependency.GitUrl is { } url)
                {
                    Out.Say(new Span($"{packageId} needs {dependency.Id} from {url}.", Tone.Dim));
                    await CloneInstallAsync(install, dependency.Id, url, false, ReviewGate($"{dependency.Id} comes from a git URL in {packageId}'s package.json, not the Basis registry, so nobody has checked it for you.", null, false, assumeYes), reviewRequired: true, assumeYes: assumeYes);
                    Out.Success($"Installed {dependency.Id}.");
                    await InstallMissingDependenciesAsync(install, catalog, dependency.Id, assumeYes, shipped, depth + 1);
                }
            }
            catch (DeclinedException) { Out.Warning($"Left {dependency.Id} uninstalled, so {packageId} may not compile."); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                DiagnosticLog.Write($"Installing {dependency.Id} for {packageId}", ex);
                Out.Error($"{dependency.Id}: {ex.Message}");
                _exitCode = 1;
            }
        }
    }

    private bool ConfirmDependents(BasisInstall install, string id, bool force)
    {
        if (force) return true;
        var dependents = PackageDependencies.FindDependents(install.UnityProjectPath, id);
        if (dependents.Count == 0) return true;
        var names = string.Join(", ", dependents.Select(d => d.Id));
        if (Console.IsInputRedirected) throw new InvalidOperationException($"{names} {(dependents.Count == 1 ? "needs" : "need")} {id}. Add --force to remove it anyway.");
        return Confirm($"{names} {(dependents.Count == 1 ? "needs" : "need")} {id} and will probably stop compiling without it. Remove it anyway?");
    }
}
