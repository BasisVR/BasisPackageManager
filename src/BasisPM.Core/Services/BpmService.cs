using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public enum BpmIssueLevel { Info, Warning, Error }

public sealed record BpmIssue(BpmIssueLevel Level, string Subject, string Message);

public sealed record BpmCheckoutState(string Name, BpmHeld Held, string Folder, bool Exists, string? Head, string? Branch, string? Locked, int Changes);

public sealed record BpmMountLine(string Id, string? Line, string Source, bool Override);

public sealed record BpmReport(string ProjectPath, string BasisDevRoot, IReadOnlyList<BpmCheckoutState> Checkouts, IReadOnlyList<BpmMountLine> Mounts, IReadOnlyList<BpmIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Level == BpmIssueLevel.Error);
}

public sealed record BpmResult(bool Ok, IReadOnlyList<BpmIssue> Issues, IReadOnlyList<string> Done)
{
    public string Summary => string.Join("\n", Done.Concat(Issues.Select(i => i.Message)));
}

public sealed record BpmPackageInfo(string Id, string Folder, string? Version, IReadOnlyDictionary<string, string> Needs, IReadOnlyDictionary<string, string> GitNeeds)
{
    public IReadOnlyDictionary<string, string> UnityNeeds { get; init; } = new Dictionary<string, string>();
}

public enum BpmTemplateChangeKind { Add, Update, Delete, Conflict, Skip }

public sealed record BpmTemplateChange(string Path, BpmTemplateChangeKind Kind, string Reason);

public sealed record BpmTemplatePlan(string Checkout, string From, string To, IReadOnlyList<BpmTemplateChange> Changes)
{
    public static readonly BpmTemplatePlan None = new("", "", "", Array.Empty<BpmTemplateChange>());
    public bool IsEmpty => Changes.Count == 0;
}

public sealed class BpmService
{
    public const string DefaultCheckoutName = "Basis";
    public const string BasisUrl = BasisInstallService.BasisRepoUrl;

    private static readonly JsonDocumentOptions JsonRead = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private readonly GitService _git;

    public BpmService(GitService git) => _git = git;

    public static string BasisDevRoot(string projectPath) => BasisDevStore.Root(projectPath);

    public static string CheckoutFolder(string projectPath, string name, BpmCheckout checkout) =>
        string.IsNullOrWhiteSpace(checkout.Path) ? Path.Combine(BasisDevRoot(projectPath), name) : Path.GetFullPath(Path.Combine(projectPath, checkout.Path));

    public static string BareRepository(string projectPath, string name, BpmCheckout checkout) =>
        Path.Combine(BasisDevRoot(projectPath), (string.IsNullOrWhiteSpace(checkout.Repository) ? name : checkout.Repository.Trim()).ToLowerInvariant() + ".git");

    public static string? DefaultCheckout(BpmManifest manifest)
    {
        if (manifest.Checkouts is not { Count: > 0 } checkouts) return null;
        if (manifest.Settings?.DefaultCheckout is { Length: > 0 } named && checkouts.ContainsKey(named)) return named;
        if (checkouts.ContainsKey(DefaultCheckoutName)) return DefaultCheckoutName;
        return checkouts.Count == 1 ? checkouts.Keys.First() : null;
    }

    public async Task<BpmResult> ResolveAsync(string projectPath, Catalog? catalog = null, bool refresh = false, Action<string>? progress = null, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var done = new List<string>();
        BpmManifest manifest;
        BpmLock previous;
        try { (manifest, previous) = (BpmStore.ReadManifest(projectPath), BpmStore.ReadLock(projectPath)); }
        catch (InvalidDataException ex) { return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, BpmStore.ManifestFileName, ex.Message) }, done); }

        var locked = new BpmLock { Checkouts = new Dictionary<string, BpmLockCheckout>(StringComparer.Ordinal), Extra = previous.Extra };
        var indexes = new Dictionary<string, IReadOnlyDictionary<string, BpmPackageInfo>>(StringComparer.Ordinal);
        foreach (var (name, checkout) in manifest.Checkouts ?? new Dictionary<string, BpmCheckout>())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"Reading checkout {name}…");
            var (entry, index, problem) = await ResolveCheckoutAsync(projectPath, name, checkout, previous.Checkouts?.GetValueOrDefault(name), refresh, progress, ct).ConfigureAwait(false);
            if (problem is not null) issues.Add(problem);
            if (entry is not null) locked.Checkouts[name] = entry;
            if (index is not null) indexes[name] = index;
        }
        issues.AddRange(SharedBranchIssues(manifest));

        var defaultCheckout = DefaultCheckout(manifest);
        var queue = new Queue<(string Id, string Version, int Depth, string? NeededBy)>();
        foreach (var (id, package) in manifest.Packages)
        {
            if (!BasisDevStore.IsValidId(id)) { issues.Add(new(BpmIssueLevel.Error, id, $"\"{id}\" isn't a valid package name.")); continue; }
            if (package.IsEnabled) queue.Enqueue((id, package.Version, 0, null));
        }
        var disabled = manifest.Packages.Where(p => !p.Value.IsEnabled).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var embedded = IndexFromDisk(projectPath, "Packages").Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (id, version, depth, neededBy) = queue.Dequeue();
            if (locked.Packages.TryGetValue(id, out var already))
            {
                already.Depth = Math.Min(already.Depth, depth);
                continue;
            }
            var listed = manifest.Packages.GetValueOrDefault(id);
            var (entry, info, problem) = await ResolvePackageAsync(projectPath, id, version, listed, previous.Packages.GetValueOrDefault(id), indexes, catalog, refresh, ct).ConfigureAwait(false);
            if (problem is not null) issues.Add(neededBy is null ? problem : problem with { Message = problem.Message + $" ({neededBy} needs it)" });
            if (entry is null) continue;
            entry.Depth = depth;
            locked.Packages[id] = entry;
            if (info is null) continue;
            var fromDefault = defaultCheckout is null ? null : indexes.GetValueOrDefault(defaultCheckout);
            var unityNeeds = info.UnityNeeds.Where(u => fromDefault?.ContainsKey(u.Key) == true && !info.Needs.ContainsKey(u.Key) && !info.GitNeeds.ContainsKey(u.Key)).ToList();
            var allNeeds = info.Needs.Concat(info.GitNeeds.Where(g => !info.Needs.ContainsKey(g.Key))).Concat(unityNeeds).ToList();
            if (allNeeds.Count > 0) entry.Dependencies = allNeeds.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
            foreach (var (need, range) in allNeeds)
            {
                if (locked.Packages.ContainsKey(need) || embedded.Contains(need) || manifest.Packages.ContainsKey(need) && !disabled.Contains(need)) continue;
                if (disabled.Contains(need)) { issues.Add(new(BpmIssueLevel.Warning, need, $"{id} needs {need}, which {BpmStore.ManifestFileName} turns off.")); continue; }
                if (info.GitNeeds.TryGetValue(need, out var gitUrl)) queue.Enqueue((need, gitUrl, depth + 1, id));
                else if (defaultCheckout is not null && indexes.GetValueOrDefault(defaultCheckout)?.ContainsKey(need) == true) queue.Enqueue((need, "checkout:" + defaultCheckout, depth + 1, id));
                else if (!need.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)) issues.Add(new(BpmIssueLevel.Warning, need, $"{id} needs {need} {range}, which no checkout or listed package provides."));
            }
        }

        var summary = $"Locked {locked.Packages.Count} package(s) from {locked.Checkouts.Count} checkout(s) in {BpmStore.LockFileName}.";
        try { BpmStore.Save(projectPath, manifest, locked); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(BpmIssueLevel.Error, BpmStore.LockFileName, ex.Message)); }
        done.Add(summary);
        return new BpmResult(!issues.Any(i => i.Level == BpmIssueLevel.Error), issues, done);
    }

    private async Task<(BpmLockCheckout? Entry, IReadOnlyDictionary<string, BpmPackageInfo>? Index, BpmIssue? Problem)> ResolveCheckoutAsync(
        string projectPath, string name, BpmCheckout checkout, BpmLockCheckout? previous, bool refresh, Action<string>? progress, CancellationToken ct)
    {
        var folder = CheckoutFolder(projectPath, name, checkout);
        switch (checkout.Kind)
        {
            case BpmHeld.Pinned:
            {
                if (string.IsNullOrWhiteSpace(checkout.Url)) return (null, null, new(BpmIssueLevel.Error, name, $"Checkout {name} is pinned but has no url."));
                if (string.IsNullOrWhiteSpace(checkout.Commit)) return (null, null, new(BpmIssueLevel.Error, name, $"Checkout {name} is pinned but has no commit."));
                if (!string.IsNullOrWhiteSpace(checkout.Path)) return (null, null, new(BpmIssueLevel.Error, name, $"Checkout {name} is pinned, so it has to live in {BasisDevStore.FolderName}. Remove its path, or hold it as a working tree."));
                var bare = BareRepository(projectPath, name, checkout);
                var ensured = await EnsureBareAsync(bare, checkout.Url.Trim(), progress, ct).ConfigureAwait(false);
                if (!ensured.Ok) return (null, null, new(BpmIssueLevel.Error, name, $"Couldn't get {checkout.Url}: {ensured.Output}"));
                var commit = await FindCommitAsync(bare, checkout.Commit.Trim(), progress, ct).ConfigureAwait(false);
                if (commit is null) return (null, null, new(BpmIssueLevel.Error, name, $"{checkout.Commit} isn't in {checkout.Url}. It may have been rewritten away."));
                var index = await IndexFromGitAsync(bare, commit, checkout.PackagesFolder, ct).ConfigureAwait(false);
                var problem = BpmVersion.IsCommitHash(checkout.Commit) ? null : new BpmIssue(BpmIssueLevel.Warning, name, $"Checkout {name} pins {checkout.Commit}, which isn't a commit hash. The lock records {Short(commit)}; put the hash in {BpmStore.ManifestFileName} so the pin can't move.");
                return (new BpmLockCheckout { Url = checkout.Url.Trim(), Commit = commit, Held = "pinned" }, index, problem);
            }
            case BpmHeld.WorkingTree:
            {
                var entry = new BpmLockCheckout { Url = Blank(checkout.Url), Branch = Blank(checkout.Branch), Held = "working-tree", Path = Blank(checkout.Path) };
                if (!Directory.Exists(folder))
                {
                    entry.Commit = previous?.Commit;
                    return (entry, null, new(BpmIssueLevel.Warning, name, $"Working tree {name} isn't at {folder} yet, so its packages can't be read. Run restore first."));
                }
                entry.Commit = _git.IsGitRepo(folder) ? await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false) : null;
                entry.Branch = (_git.IsGitRepo(folder) ? await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false) : null) ?? entry.Branch;
                return (entry, IndexFromDisk(folder, checkout.PackagesFolder), null);
            }
            default:
                if (!Directory.Exists(folder)) return (new BpmLockCheckout { Held = "inline", Path = Blank(checkout.Path) }, null, new(BpmIssueLevel.Error, name, $"Checkout {name} points at {folder}, which doesn't exist."));
                return (new BpmLockCheckout { Held = "inline", Path = Blank(checkout.Path) }, IndexFromDisk(folder, checkout.PackagesFolder), null);
        }
    }

    private async Task<(BpmLockPackage? Entry, BpmPackageInfo? Info, BpmIssue? Problem)> ResolvePackageAsync(string projectPath, string id, string version,
        BpmManifestPackage? listed, BpmLockPackage? previous, Dictionary<string, IReadOnlyDictionary<string, BpmPackageInfo>> indexes, Catalog? catalog, bool refresh, CancellationToken ct)
    {
        var wanted = BpmVersion.Parse(version);
        switch (wanted.Kind)
        {
            case BpmVersionKind.Checkout:
                if (!indexes.TryGetValue(wanted.Value, out var index))
                    return (previous, null, new(BpmIssueLevel.Error, id, $"{id} comes from checkout {wanted.Value}, which couldn't be read."));
                if (!index.TryGetValue(id, out var info)) return (null, null, new(BpmIssueLevel.Error, id, $"Checkout {wanted.Value} has no package called {id}."));
                return (new BpmLockPackage { Version = "checkout:" + wanted.Value, Requested = version, Folder = info.Folder, PackageVersion = info.Version, ForkOf = Blank(listed?.ForkOf) }, info, null);
            case BpmVersionKind.Git:
            {
                if (!GitUrlPolicy.IsSafeRef(wanted.Ref) || !GitUrlPolicy.IsSafeSubPath(wanted.SubPath) || !_git.CanFetch(wanted.Url))
                    return (null, null, new(BpmIssueLevel.Error, id, $"{version} isn't a git source BPM will fetch."));
                var clone = BasisDevStore.CloneFolder(projectPath, id);
                var commit = BpmVersion.IsCommitHash(wanted.Ref) ? wanted.Ref : null;
                if (commit is null && !refresh && previous is not null && previous.Requested == version && BpmVersion.Parse(previous.Version) is { Kind: BpmVersionKind.Git } old && BpmVersion.IsCommitHash(old.Ref)) commit = old.Ref;
                if (commit is null && MountService.IsWorkingClone(clone) && !refresh) commit = await _git.ResolveCommitAsync(clone, wanted.Ref ?? "HEAD", ct).ConfigureAwait(false);
                commit ??= await _git.ResolveRemoteRefAsync(wanted.Url!, wanted.Ref, ct).ConfigureAwait(false);
                if (commit is null) return (null, null, new(BpmIssueLevel.Error, id, $"Couldn't find {wanted.Ref ?? "the default branch"} in {wanted.Url}."));
                var root = MountService.IsWorkingClone(clone) ? BasisDevStore.PackageRoot(clone, wanted.SubPath) : null;
                var read = root is not null ? ReadPackage(Path.Combine(root, "package.json"), id, "") : null;
                return (new BpmLockPackage { Version = BpmVersion.Git(wanted.Url!, wanted.SubPath, commit), Requested = version, PackageVersion = read?.Version ?? previous?.PackageVersion, ForkOf = Blank(listed?.ForkOf), Dependencies = previous?.Dependencies }, read, null);
            }
            case BpmVersionKind.File:
            {
                var full = Path.GetFullPath(Path.Combine(projectPath, wanted.Value));
                var read = ReadPackage(Path.Combine(full, "package.json"), id, "");
                if (read is null) return (null, null, new(BpmIssueLevel.Error, id, $"{wanted.Value} has no package.json for {id}."));
                return (new BpmLockPackage { Version = "file:" + wanted.Value.Replace('\\', '/'), Requested = version, PackageVersion = read.Version }, read, null);
            }
            case BpmVersionKind.Registry:
            {
                if (catalog?.Packages.GetValueOrDefault(id) is not { } entry) return (null, null, new(BpmIssueLevel.Error, id, $"{id} {version} needs the package registry, which couldn't be read."));
                var range = SemVerRange.TryParse(version, out var parsed) ? parsed : null;
                var pick = entry.Versions.Where(v => SemVer.TryParse(v.Key, out var sv) && (range is null ? v.Key == version : range.Satisfies(sv)))
                    .OrderByDescending(v => SemVer.Parse(v.Key)).Select(v => v.Value).FirstOrDefault();
                if (pick is null || UpmGitUrl.Parse(pick.Url) is not { } source) return (null, null, new(BpmIssueLevel.Error, id, $"The registry has no release of {id} that matches {version}."));
                var commit = Blank(pick.Commit) ?? await _git.ResolveRemoteRefAsync(source.CloneUrl, pick.Ref ?? source.Ref, ct).ConfigureAwait(false);
                if (commit is null) return (null, null, new(BpmIssueLevel.Error, id, $"Couldn't find the commit of {id} {pick.Version}."));
                var needs = (pick.Dependencies ?? new Dictionary<string, string>()).Where(d => !d.Key.StartsWith("com.unity.", StringComparison.OrdinalIgnoreCase)).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal);
                var registryInfo = new BpmPackageInfo(id, "", pick.Version, needs, pick.GitDependencies ?? new Dictionary<string, string>());
                return (new BpmLockPackage { Version = BpmVersion.Git(source.CloneUrl, source.Path, commit), Requested = version, PackageVersion = pick.Version, ForkOf = Blank(listed?.ForkOf) }, registryInfo, null);
            }
            default:
                return (null, null, new(BpmIssueLevel.Error, id, $"\"{version}\" isn't a version BPM understands."));
        }
    }

    public async Task<BpmResult> RestoreAsync(string projectPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var done = new List<string>();
        BpmManifest manifest;
        BpmLock locked;
        try { (manifest, locked) = (BpmStore.ReadManifest(projectPath), BpmStore.ReadLock(projectPath)); }
        catch (InvalidDataException ex) { return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, BpmStore.ManifestFileName, ex.Message) }, done); }

        foreach (var name in (locked.Checkouts?.Keys ?? Enumerable.Empty<string>()).Where(n => manifest.Checkouts?.ContainsKey(n) != true))
            issues.Add(new(BpmIssueLevel.Warning, name, $"{BpmStore.LockFileName} lists checkout {name}, but {BpmStore.ManifestFileName} doesn't. Lock again."));
        var sharedBranch = SharedBranchIssues(manifest);
        issues.AddRange(sharedBranch);
        var blocked = sharedBranch.Select(i => i.Subject).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, checkout) in manifest.Checkouts ?? new Dictionary<string, BpmCheckout>())
        {
            ct.ThrowIfCancellationRequested();
            if (blocked.Contains(name)) continue;
            var entry = locked.Checkouts?.GetValueOrDefault(name)
                ?? (checkout.Kind == BpmHeld.Pinned ? null : new BpmLockCheckout { Url = Blank(checkout.Url), Branch = Blank(checkout.Branch), Held = checkout.Kind == BpmHeld.WorkingTree ? "working-tree" : "inline", Path = Blank(checkout.Path) });
            if (entry is null)
            {
                issues.Add(new(BpmIssueLevel.Error, name, $"Checkout {name} isn't in {BpmStore.LockFileName} yet. Lock first."));
                continue;
            }
            var folder = CheckoutFolder(projectPath, name, checkout);
            switch (checkout.Kind)
            {
                case BpmHeld.Pinned:
                    progress?.Invoke($"Restoring {name} at {Short(entry.Commit)}…");
                    var pinned = await RestorePinnedAsync(projectPath, name, checkout, entry, SparseFolders(manifest, locked, name, checkout), progress, ct).ConfigureAwait(false);
                    if (pinned.Ok) done.Add(pinned.Output);
                    else issues.Add(new(BpmIssueLevel.Error, name, pinned.Output));
                    break;
                case BpmHeld.WorkingTree:
                    var tree = await RestoreWorkingTreeAsync(projectPath, name, checkout, entry, manifest, progress, ct).ConfigureAwait(false);
                    if (tree.Problem is not null) issues.Add(tree.Problem);
                    if (tree.Done is not null) done.Add(tree.Done);
                    break;
                default:
                    if (!Directory.Exists(folder)) issues.Add(new(BpmIssueLevel.Error, name, $"Checkout {name} should be at {folder}, which doesn't exist. BPM doesn't create folders outside {BasisDevStore.FolderName}."));
                    break;
            }
        }
        foreach (var (id, package) in locked.Packages)
        {
            if (BpmVersion.Parse(package.Version) is not { Kind: BpmVersionKind.File } file) continue;
            if (!File.Exists(Path.Combine(projectPath, file.Value, "package.json"))) issues.Add(new(BpmIssueLevel.Error, id, $"{id} should be in {file.Value}, which has no package.json."));
        }
        return new BpmResult(!issues.Any(i => i.Level == BpmIssueLevel.Error), issues, done);
    }

    public static IReadOnlyList<BpmIssue> SharedBranchIssues(BpmManifest manifest)
    {
        var issues = new List<BpmIssue>();
        var trees = (manifest.Checkouts ?? new Dictionary<string, BpmCheckout>()).Where(c => c.Value.Kind == BpmHeld.WorkingTree && !string.IsNullOrWhiteSpace(c.Value.Repository) && !string.IsNullOrWhiteSpace(c.Value.Branch));
        foreach (var group in trees.GroupBy(c => (Repository: c.Value.Repository!.Trim().ToLowerInvariant(), Branch: c.Value.Branch!.Trim())).Where(g => g.Count() > 1))
        {
            var names = group.Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
            foreach (var name in names.Skip(1))
                issues.Add(new(BpmIssueLevel.Error, name, $"{name} and {names[0]} both want branch {group.Key.Branch} of {group.Key.Repository}.git. Git won't check out one branch in two worktrees, so give each its own branch."));
        }
        return issues;
    }

    public static IReadOnlyList<string> SparseFolders(BpmManifest manifest, BpmLock locked, string name, BpmCheckout checkout)
    {
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var package in locked.Packages.Values)
            if (BpmVersion.Parse(package.Version) is { Kind: BpmVersionKind.Checkout } from && from.Value == name && !string.IsNullOrWhiteSpace(package.Folder)) folders.Add(package.Folder.Trim('/'));
        foreach (var extra in checkout.Sparse ?? new List<string>()) if (!string.IsNullOrWhiteSpace(extra)) folders.Add(extra.Replace('\\', '/').Trim('/'));
        if (string.Equals(manifest.Settings?.Template, name, StringComparison.Ordinal))
            foreach (var part in TemplateFolders(checkout)) folders.Add(part);
        if (folders.Count == 0) folders.Add(checkout.PackagesFolder + "/.bpm-none");
        return folders.ToList();
    }

    public static IReadOnlyList<string> TemplateFolders(BpmCheckout checkout)
    {
        var project = ProjectFolderOf(checkout);
        return new[] { Join(project, "Assets"), Join(project, "ProjectSettings") };
    }

    private static string ProjectFolderOf(BpmCheckout checkout)
    {
        var packages = checkout.PackagesFolder;
        var slash = packages.LastIndexOf('/');
        return slash < 0 ? "" : packages[..slash];
    }

    private static string Join(string a, string b) => a.Length == 0 ? b : a + "/" + b;

    private async Task<GitResult> RestorePinnedAsync(string projectPath, string name, BpmCheckout checkout, BpmLockCheckout entry, IReadOnlyList<string> sparse, Action<string>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entry.Url) || !BpmVersion.IsCommitHash(entry.Commit)) return new GitResult(false, -1, $"{BpmStore.LockFileName} has no commit for {name}. Lock first.");
        var bare = BareRepository(projectPath, name, checkout);
        var folder = CheckoutFolder(projectPath, name, checkout);
        var ensured = await EnsureBareAsync(bare, entry.Url, progress, ct).ConfigureAwait(false);
        if (!ensured.Ok) return ensured;
        var commit = await FindCommitAsync(bare, entry.Commit!, progress, ct).ConfigureAwait(false);
        if (commit is null) return new GitResult(false, -1, $"Commit {entry.Commit} of {name} isn't in {entry.Url} any more, so it can't be restored. It was probably rewritten away; move the pin.");

        var created = false;
        if (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any())
        {
            await _git.PruneWorktreesAsync(bare, ct).ConfigureAwait(false);
            var add = await _git.AddWorktreeAsync(bare, folder, commit, null, null, ct).ConfigureAwait(false);
            if (!add.Ok) return new GitResult(false, add.Code, $"Couldn't create {folder}: {add.Output}");
            created = true;
        }
        else if (!_git.IsGitRepo(folder)) return new GitResult(false, -1, $"{folder} exists but isn't a git checkout, so it was left alone.");
        await _git.SetWorktreeConfigAsync(folder, "core.bare", "false", ct).ConfigureAwait(false);

        var head = created ? null : await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
        var changes = created ? 0 : (await _git.GetChangesAsync(folder, ct).ConfigureAwait(false)).Count;
        var current = created ? Array.Empty<string>() : await _git.GetConeSparseFoldersAsync(folder, ct).ConfigureAwait(false);
        var sparseChanged = !current.Order(StringComparer.Ordinal).SequenceEqual(sparse.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        if (!created && head == commit && !sparseChanged) return new GitResult(true, 0, $"{name} is already at {Short(commit)}.");
        if (changes > 0) return new GitResult(false, -1, $"{name} has {changes} edited file(s), so it wasn't moved. A pinned checkout belongs to BPM: move edits into a working tree, then restore again.");

        var set = await _git.SetConeSparseCheckoutAsync(folder, sparse, ct).ConfigureAwait(false);
        if (!set.Ok) return new GitResult(false, set.Code, $"Couldn't choose the folders of {name}: {set.Output}");
        var reset = await _git.HardResetAsync(folder, commit, progress, ct).ConfigureAwait(false);
        if (!reset.Ok) return new GitResult(false, reset.Code, $"Couldn't check out {name} at {Short(commit)}: {reset.Output}");
        return new GitResult(true, 0, created ? $"Checked out {name} at {Short(commit)} ({sparse.Count} folder(s))." : head == commit ? $"Updated which folders {name} holds." : $"Moved {name} from {Short(head)} to {Short(commit)}.");
    }

    private async Task<(BpmIssue? Problem, string? Done)> RestoreWorkingTreeAsync(string projectPath, string name, BpmCheckout checkout, BpmLockCheckout entry, BpmManifest manifest, Action<string>? progress, CancellationToken ct)
    {
        var folder = CheckoutFolder(projectPath, name, checkout);
        if (Directory.Exists(folder))
        {
            if (!_git.IsGitRepo(folder)) return (null, $"{name} is at {folder} (not a git repository).");
            var branch = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false);
            var head = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
            var changes = (await _git.GetChangesAsync(folder, ct).ConfigureAwait(false)).Count;
            var where = $"{name} is on {branch ?? "a detached HEAD"} at {Short(head)}" + (changes > 0 ? $" with {changes} uncommitted change(s)" : "") + ". Left as it is.";
            if (entry.Commit is { } lockedAt && head != lockedAt && await _git.ResolveCommitAsync(folder, lockedAt, ct).ConfigureAwait(false) is null)
                return (new(BpmIssueLevel.Warning, name, where + $" The lock names {Short(lockedAt)}, which this clone doesn't have; fetch it."), null);
            return (null, where);
        }
        if (!string.IsNullOrWhiteSpace(checkout.Path)) return (new(BpmIssueLevel.Error, name, $"Working tree {name} should be at {folder}, which doesn't exist. BPM doesn't create folders outside {BasisDevStore.FolderName}."), null);
        var url = Blank(entry.Url) ?? Blank(checkout.Url);
        var wantedBranch = Blank(entry.Branch) ?? Blank(checkout.Branch);
        if (url is null || wantedBranch is null || !GitUrlPolicy.IsSafeRef(wantedBranch)) return (new(BpmIssueLevel.Error, name, $"Working tree {name} needs a url and a branch to be created."), null);

        progress?.Invoke($"Creating working tree {name} on {wantedBranch}…");
        if (!string.IsNullOrWhiteSpace(checkout.Repository))
        {
            var bare = BareRepository(projectPath, name, checkout);
            if (!Directory.Exists(bare)) return (new(BpmIssueLevel.Error, name, $"{name} shares {Path.GetFileName(bare)}, which doesn't exist yet. Restore the checkout that owns it first."), null);
            var remote = RemoteName(name);
            var setRemote = await _git.SetRemoteAsync(bare, remote, url, ct).ConfigureAwait(false);
            if (!setRemote.Ok) return (new(BpmIssueLevel.Error, name, setRemote.Output), null);
            var fetch = await _git.FetchBranchAsync(bare, url, wantedBranch, $"refs/remotes/{remote}/{wantedBranch}", progress, ct).ConfigureAwait(false);
            if (!fetch.Ok) return (new(BpmIssueLevel.Error, name, $"Couldn't fetch {wantedBranch} from {url}: {fetch.Output}"), null);
            var exists = await _git.ResolveCommitAsync(bare, "refs/heads/" + wantedBranch, ct).ConfigureAwait(false) is not null;
            await _git.PruneWorktreesAsync(bare, ct).ConfigureAwait(false);
            var add = exists
                ? await _git.AddWorktreeAsync(bare, folder, null, wantedBranch, null, ct).ConfigureAwait(false)
                : await _git.AddWorktreeAsync(bare, folder, null, wantedBranch, $"refs/remotes/{remote}/{wantedBranch}", ct).ConfigureAwait(false);
            if (!add.Ok) return (new(BpmIssueLevel.Error, name, $"Couldn't create {folder}: {add.Output}"), null);
            await _git.SetWorktreeConfigAsync(folder, "core.bare", "false", ct).ConfigureAwait(false);
            if (checkout.Sparse is { Count: > 0 } sparse)
            {
                var set = await _git.SetConeSparseCheckoutAsync(folder, sparse, ct).ConfigureAwait(false);
                if (!set.Ok) return (new(BpmIssueLevel.Error, name, set.Output), null);
            }
            var tip = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
            var reset = await _git.HardResetAsync(folder, tip!, progress, ct).ConfigureAwait(false);
            if (!reset.Ok) return (new(BpmIssueLevel.Error, name, reset.Output), null);
        }
        else
        {
            Directory.CreateDirectory(BasisDevRoot(projectPath));
            var clone = await _git.CloneAsync(url, folder, wantedBranch, progress, ct).ConfigureAwait(false);
            if (!clone.Ok) return (new(BpmIssueLevel.Error, name, $"Couldn't clone {url} ({wantedBranch}): {clone.Output}"), null);
            if (checkout.Sparse is { Count: > 0 } sparse)
            {
                var set = await _git.SetConeSparseCheckoutAsync(folder, sparse, ct).ConfigureAwait(false);
                if (!set.Ok) return (new(BpmIssueLevel.Error, name, set.Output), null);
            }
        }
        var now = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
        var note = entry.Commit is { } lockedCommit && lockedCommit != now ? $" The lock was written at {Short(lockedCommit)}; the branch is at {Short(now)} now." : "";
        return (null, $"Created working tree {name} on {wantedBranch} at {Short(now)}.{note}");
    }

    public async Task<BpmResult> MountAsync(string projectPath, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var done = new List<string>();
        BpmManifest manifest;
        BpmLock locked;
        try { (manifest, locked) = (BpmStore.ReadManifest(projectPath), BpmStore.ReadLock(projectPath)); }
        catch (InvalidDataException ex) { return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, BpmStore.ManifestFileName, ex.Message) }, done); }
        var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(manifestPath)) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, "manifest.json", $"{manifestPath} doesn't exist.") }, done);

        var lines = DesiredLines(projectPath, manifest, locked, issues);
        var root = BasisDevRoot(projectPath);
        var local = BpmStore.LocalProject(BpmStore.ReadLocal(root), root, projectPath);
        if (ReadUnityManifest(manifestPath) is not { } unityManifest) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, "manifest.json", $"{manifestPath} isn't valid JSON, so it was left unchanged.") }, done);
        var dependencies = unityManifest.Dependencies;
        var changed = 0;
        foreach (var mount in lines)
        {
            if (mount.Line is null) continue;
            if (!Exists(projectPath, mount.Line))
            {
                issues.Add(new(BpmIssueLevel.Error, mount.Id, $"{mount.Id} should load from {mount.Line[5..]}, which isn't there yet, so its line in Packages/manifest.json was left as it is. Run restore."));
                continue;
            }
            if (dependencies.TryGetValue(mount.Id, out var current) && current == mount.Line) continue;
            dependencies[mount.Id] = mount.Line;
            changed++;
        }
        var wanted = lines.Where(l => l.Line is not null).ToDictionary(l => l.Id, l => l.Line!, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, line) in local.Mounted ?? new Dictionary<string, string>())
        {
            if (wanted.ContainsKey(id) || !dependencies.TryGetValue(id, out var current) || current != line) continue;
            dependencies.Remove(id);
            changed++;
            done.Add($"Unmounted {id}.");
        }
        foreach (var embedded in lines.Where(l => l.Line is not null && Directory.Exists(Path.Combine(projectPath, "Packages", l.Id))))
            issues.Add(new(BpmIssueLevel.Warning, embedded.Id, $"Packages/{embedded.Id} is embedded, and Unity uses it instead of the mount line."));
        if (changed > 0) await UnityProjectService.SaveManifestAsync(projectPath, unityManifest, ct).ConfigureAwait(false);
        BpmStore.UpdateLocal(root, projectPath, project => project.Mounted = wanted.Count == 0 ? null : new Dictionary<string, string>(wanted, StringComparer.Ordinal));
        var overrides = lines.Count(l => l.Override);
        done.Add(changed == 0 ? "Packages/manifest.json already mounts everything in the lock." : $"Wrote {changed} line(s) in Packages/manifest.json.");
        if (overrides > 0) issues.Add(new(BpmIssueLevel.Warning, "overrides", $"{overrides} local override(s) change Packages/manifest.json on this machine only. Don't commit those lines."));
        return new BpmResult(!issues.Any(i => i.Level == BpmIssueLevel.Error), issues, done);
    }

    private static bool Exists(string projectPath, string line)
    {
        var target = Path.GetFullPath(Path.Combine(projectPath, "Packages", line[5..].Replace('/', Path.DirectorySeparatorChar)));
        return File.Exists(target) || File.Exists(Path.Combine(target, "package.json"));
    }

    public static IReadOnlyList<BpmMountLine> DesiredLines(string projectPath, BpmManifest manifest, BpmLock locked, List<BpmIssue>? issues = null)
    {
        var root = BasisDevRoot(projectPath);
        var overrides = BpmStore.LocalProject(BpmStore.ReadLocal(root), root, projectPath).Overrides ?? new Dictionary<string, string>();
        var packagesFolder = Path.Combine(Path.GetFullPath(projectPath), "Packages");
        var result = new List<BpmMountLine>();
        foreach (var (id, package) in locked.Packages.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (overrides.TryGetValue(id, out var overridePath))
            {
                var full = Path.GetFullPath(Path.Combine(projectPath, overridePath));
                if (!File.Exists(Path.Combine(full, "package.json"))) issues?.Add(new(BpmIssueLevel.Error, id, $"The local override for {id} points at {full}, which has no package.json."));
                result.Add(new(id, BasisDevStore.ManifestLine(projectPath, full), "override " + overridePath, true));
                continue;
            }
            var version = BpmVersion.Parse(package.Version);
            switch (version.Kind)
            {
                case BpmVersionKind.Checkout:
                    if (manifest.Checkouts?.GetValueOrDefault(version.Value) is not { } checkout || string.IsNullOrWhiteSpace(package.Folder))
                    {
                        issues?.Add(new(BpmIssueLevel.Error, id, $"{id} comes from checkout {version.Value}, which {BpmStore.ManifestFileName} doesn't define."));
                        continue;
                    }
                    var folder = Path.Combine(CheckoutFolder(projectPath, version.Value, checkout), package.Folder.Replace('/', Path.DirectorySeparatorChar));
                    result.Add(new(id, BasisDevStore.ManifestLine(projectPath, folder), version.Value, false));
                    break;
                case BpmVersionKind.Git:
                    var clone = BasisDevStore.CloneFolder(projectPath, id);
                    var packageRoot = string.IsNullOrEmpty(version.SubPath) ? clone : Path.Combine(clone, version.SubPath.Replace('/', Path.DirectorySeparatorChar));
                    result.Add(new(id, BasisDevStore.ManifestLine(projectPath, packageRoot), "clone", false));
                    break;
                case BpmVersionKind.File:
                    var target = Path.GetFullPath(Path.Combine(projectPath, version.Value));
                    var embedded = BasisDevStore.SamePath(Path.GetDirectoryName(target), packagesFolder);
                    result.Add(new(id, embedded ? null : BasisDevStore.ManifestLine(projectPath, target), embedded ? "embedded" : "file", false));
                    break;
            }
        }
        return result;
    }

    public void SetOverride(string projectPath, string id, string? path)
    {
        if (!BasisDevStore.IsValidId(id)) throw new ArgumentException($"\"{id}\" isn't a valid package name.");
        var root = BasisDevRoot(projectPath);
        string? relative = null;
        if (path is not null)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(Path.Combine(full, "package.json"))) throw new ArgumentException($"{full} has no package.json.");
            relative = Path.GetRelativePath(Path.GetFullPath(projectPath), full).Replace('\\', '/');
        }
        BpmStore.UpdateLocal(root, projectPath, project =>
        {
            project.Overrides ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (relative is null) project.Overrides.Remove(id);
            else project.Overrides[id] = relative;
        });
    }

    public static void SetEnabled(string projectPath, string id, bool enabled)
    {
        BpmStore.Update(projectPath, (manifest, _) =>
        {
            var key = manifest.Packages.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{id} isn't listed in {BpmStore.ManifestFileName}.");
            manifest.Packages[key].Enabled = enabled ? "yes" : "no";
        });
    }

    public static BpmResult Embed(string projectPath, string id)
    {
        var fail = (string message) => new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, id, message) }, Array.Empty<string>());
        BpmManifest manifest;
        BpmLock locked;
        try { (manifest, locked) = (BpmStore.ReadManifest(projectPath), BpmStore.ReadLock(projectPath)); }
        catch (InvalidDataException ex) { return fail(ex.Message); }
        var key = locked.Packages.Keys.FirstOrDefault(k => string.Equals(k, id, StringComparison.OrdinalIgnoreCase));
        if (key is null) return fail($"{id} isn't in {BpmStore.LockFileName}. Lock first.");
        var package = locked.Packages[key];
        var version = BpmVersion.Parse(package.Version);
        string source, url, commit, subPath;
        switch (version.Kind)
        {
            case BpmVersionKind.Checkout:
                if (manifest.Checkouts?.GetValueOrDefault(version.Value) is not { } checkout || string.IsNullOrWhiteSpace(package.Folder)) return fail($"{key} comes from checkout {version.Value}, which {BpmStore.ManifestFileName} doesn't define.");
                if (locked.Checkouts?.GetValueOrDefault(version.Value)?.Commit is not { } at || string.IsNullOrWhiteSpace(checkout.Url)) return fail($"Checkout {version.Value} has no url and locked commit, so the copy couldn't say where it came from.");
                source = Path.Combine(CheckoutFolder(projectPath, version.Value, checkout), package.Folder.Replace('/', Path.DirectorySeparatorChar));
                (url, commit, subPath) = (checkout.Url.Trim(), at, package.Folder.Trim('/'));
                break;
            case BpmVersionKind.Git when BpmVersion.IsCommitHash(version.Ref):
                if (BasisDevStore.PackageRoot(BasisDevStore.CloneFolder(projectPath, key), version.SubPath) is not { } clonedRoot) return fail($"{package.Version} has a path BPM won't follow.");
                source = clonedRoot;
                (url, commit, subPath) = (version.Url!, version.Ref!, version.SubPath ?? "");
                break;
            default:
                return fail($"{key} is {package.Version}, which is already a folder of yours.");
        }
        if (source.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return fail($"{key} is a .tgz in its checkout, so there's no folder to copy. Unpack it into Packages/ by hand.");
        if (!File.Exists(Path.Combine(source, "package.json"))) return fail($"{source} has no package.json. Run restore first.");
        var folderName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var target = Path.Combine(projectPath, "Packages", folderName);
        if (Directory.Exists(target)) return fail($"Packages/{folderName} already exists. Move it away first.");
        CopyFolder(source, target, skipGit: true);
        BpmStore.Update(projectPath, (m, _) =>
        {
            var listed = m.Packages.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            var entry = listed is null ? new BpmManifestPackage() : m.Packages[listed];
            entry.Version = "file:Packages/" + folderName;
            entry.CopiedFrom = BpmVersion.Git(url, subPath, commit);
            m.Packages[listed ?? key] = entry;
        });
        return new BpmResult(true, Array.Empty<BpmIssue>(), new[] { $"Copied {key} into Packages/{folderName} from {Short(commit)}. Its mount line goes away, and {BpmStore.ManifestFileName} records where it came from so it can be diffed and sent back." });
    }

    public async Task<BpmReport> StatusAsync(string projectPath, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var checkouts = new List<BpmCheckoutState>();
        var root = BasisDevRoot(projectPath);
        BpmManifest manifest;
        BpmLock locked;
        try { (manifest, locked) = (BpmStore.ReadManifest(projectPath), BpmStore.ReadLock(projectPath)); }
        catch (InvalidDataException ex) { return new BpmReport(projectPath, root, checkouts, Array.Empty<BpmMountLine>(), new[] { new BpmIssue(BpmIssueLevel.Error, BpmStore.ManifestFileName, ex.Message) }); }

        if (File.Exists(BpmStore.LegacyPath(projectPath))) issues.Add(new(BpmIssueLevel.Info, BpmStore.LegacyFileName, $"Packages/{BpmStore.LegacyFileName} is still the package list. The next change replaces it with {BpmStore.ManifestFileName} and {BpmStore.LockFileName}."));
        foreach (var (id, package) in manifest.Packages.Where(p => p.Value.IsEnabled))
        {
            if (!locked.Packages.TryGetValue(id, out var entry)) issues.Add(new(BpmIssueLevel.Warning, id, $"{id} is in {BpmStore.ManifestFileName} but not in {BpmStore.LockFileName}. Lock again."));
            else if (entry.Requested is { } requested && requested != package.Version) issues.Add(new(BpmIssueLevel.Warning, id, $"{id} asks for {package.Version}, but the lock was made from {requested}. Lock again."));
        }
        foreach (var id in manifest.Packages.Where(p => !p.Value.IsEnabled).Select(p => p.Key).Where(locked.Packages.ContainsKey))
            issues.Add(new(BpmIssueLevel.Warning, id, $"{id} is turned off but still locked. Lock again."));
        foreach (var (id, package) in manifest.Packages.Where(p => !string.IsNullOrWhiteSpace(p.Value.CopiedFrom)))
            issues.Add(new(BpmIssueLevel.Info, id, $"{id} is your copy in {BpmVersion.Parse(package.Version).Value}, taken from {package.CopiedFrom}. Diff it against that commit to send changes back."));
        issues.AddRange(SharedBranchIssues(manifest));
        issues.AddRange(BasisConfigService.Check(projectPath).Where(i => i.Subject != "server"));
        if (Directory.Exists(root))
            foreach (var file in BpmIgnore.Files(root).Where(f => !f.Covered))
                issues.Add(new(BpmIssueLevel.Warning, BasisDevStore.FolderName, $"{file.Vcs} doesn't ignore {BasisDevStore.FolderName}. Add \"{file.Line}\" to {file.Path}, or anything in it gets committed."));

        string? pinCommit = null;
        var defaultName = DefaultCheckout(manifest);
        foreach (var (name, checkout) in manifest.Checkouts ?? new Dictionary<string, BpmCheckout>())
        {
            var folder = CheckoutFolder(projectPath, name, checkout);
            var entry = locked.Checkouts?.GetValueOrDefault(name);
            var exists = Directory.Exists(folder);
            string? head = null, branch = null;
            var changes = 0;
            if (exists && _git.IsGitRepo(folder))
            {
                head = await _git.ResolveCommitAsync(folder, "HEAD", ct).ConfigureAwait(false);
                branch = await _git.GetCurrentBranchAsync(folder, ct).ConfigureAwait(false);
                changes = (await _git.GetChangesAsync(folder, ct).ConfigureAwait(false)).Count;
            }
            checkouts.Add(new(name, checkout.Kind, folder, exists, head, branch, entry?.Commit, changes));
            if (entry is null) { issues.Add(new(BpmIssueLevel.Warning, name, $"Checkout {name} isn't in {BpmStore.LockFileName}. Lock again.")); continue; }
            if (name == defaultName && checkout.Kind == BpmHeld.Pinned) pinCommit = entry.Commit;
            if (!exists) { issues.Add(new(checkout.Kind == BpmHeld.Pinned ? BpmIssueLevel.Warning : BpmIssueLevel.Error, name, $"{name} isn't at {folder}. Run restore.")); continue; }
            switch (checkout.Kind)
            {
                case BpmHeld.Pinned:
                    if (head != entry.Commit) issues.Add(new(BpmIssueLevel.Warning, name, $"{name} is at {Short(head)}, but the lock pins {Short(entry.Commit)}. Run restore."));
                    if (changes > 0) issues.Add(new(BpmIssueLevel.Warning, name, $"{name} is pinned and has {changes} edited file(s). The project's version control ignores {BasisDevStore.FolderName}, so those edits never reach anyone else."));
                    break;
                case BpmHeld.WorkingTree:
                    if (entry.Commit is { } lockedAt && head is not null && lockedAt != head)
                        issues.Add(new(BpmIssueLevel.Info, name, $"{name} is at {Short(head)}; the lock was written at {Short(lockedAt)}. Lock again before committing the lock, and push {branch ?? "the branch"} first so teammates can fetch it."));
                    break;
            }
        }
        if (pinCommit is not null && defaultName is not null)
            foreach (var (name, checkout) in (manifest.Checkouts ?? new()).Where(c => c.Value.Kind == BpmHeld.WorkingTree && string.Equals(c.Value.Repository, manifest.Checkouts![defaultName].Repository ?? defaultName, StringComparison.OrdinalIgnoreCase)))
            {
                var folder = CheckoutFolder(projectPath, name, checkout);
                if (!Directory.Exists(folder) || !_git.IsGitRepo(folder)) continue;
                if (await _git.ResolveCommitAsync(folder, pinCommit, ct).ConfigureAwait(false) is null) continue;
                var mergeBase = await _git.GetMergeBaseAsync(folder, "HEAD", pinCommit, ct).ConfigureAwait(false);
                if (mergeBase != pinCommit) issues.Add(new(BpmIssueLevel.Warning, name, $"{name} isn't rebased onto the {defaultName} pin {Short(pinCommit)} (it's based on {Short(mergeBase)}), so its packages and the upstream ones may not fit together."));
            }

        var mounts = DesiredLines(projectPath, manifest, locked, issues);
        var manifestPath = Path.Combine(projectPath, "Packages", "manifest.json");
        if (File.Exists(manifestPath))
        {
            var dependencies = ReadUnityManifest(manifestPath)?.Dependencies ?? new Dictionary<string, string>();
            foreach (var mount in mounts.Where(m => m.Line is not null))
            {
                dependencies.TryGetValue(mount.Id, out var current);
                if (mount.Source == "clone" && !MountService.IsWorkingClone(BasisDevStore.CloneFolder(projectPath, mount.Id))) issues.Add(new(BpmIssueLevel.Warning, mount.Id, $"{mount.Id} isn't cloned into {BasisDevStore.FolderName} yet. Run restore."));
                if (current != mount.Line) issues.Add(new(BpmIssueLevel.Warning, mount.Id, current is null ? $"{mount.Id} isn't mounted in Packages/manifest.json. Run mount." : $"Packages/manifest.json loads {mount.Id} from {current}, not {mount.Line}. Run mount."));
            }
        }
        if (mounts.Any(m => m.Override)) issues.Add(new(BpmIssueLevel.Info, "overrides", "Local overrides are active, so Packages/manifest.json differs from what's committed on this machine. Don't commit those lines."));
        issues.AddRange(await CompareServerLockAsync(projectPath, locked, ct).ConfigureAwait(false));
        return new BpmReport(projectPath, root, checkouts, mounts, issues);
    }

    public async Task<IReadOnlyList<BpmIssue>> CompareServerLockAsync(string projectPath, BpmLock locked, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var clone = BasisDevLocator.CloneRoot(projectPath) ?? Path.GetDirectoryName(Path.GetFullPath(projectPath));
        if (clone is null || !File.Exists(ServerPackageService.LockPath(clone))) return issues;
        IReadOnlyDictionary<string, ServerPackageLockEntry> server;
        try { server = new ServerPackageService(_git).LoadLock(clone); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return issues; }
        foreach (var (id, entry) in locked.Packages)
        {
            if (!server.TryGetValue(id, out var serverEntry) || string.IsNullOrWhiteSpace(serverEntry.Commit)) continue;
            var client = BpmVersion.Parse(entry.Version);
            var clientCommit = client.Kind == BpmVersionKind.Git && BpmVersion.IsCommitHash(client.Ref) ? client.Ref
                : client.Kind == BpmVersionKind.Checkout ? locked.Checkouts?.GetValueOrDefault(client.Value)?.Commit : null;
            if (clientCommit is null) continue;
            if (!clientCommit.StartsWith(serverEntry.Commit, StringComparison.OrdinalIgnoreCase) && !serverEntry.Commit.StartsWith(clientCommit, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(BpmIssueLevel.Warning, id, $"The client locks {id} at {Short(clientCommit)} but the Basis Server locks it at {Short(serverEntry.Commit)}. Both halves have to come from the same commit."));
        }
        return issues;
    }

    public async Task<BpmTemplatePlan> PlanTemplateAsync(string projectPath, string checkoutName, string toCommit, CancellationToken ct = default)
    {
        var manifest = BpmStore.ReadManifest(projectPath);
        var locked = BpmStore.ReadLock(projectPath);
        if (!string.Equals(manifest.Settings?.Template, checkoutName, StringComparison.Ordinal)) return BpmTemplatePlan.None;
        if (manifest.Checkouts?.GetValueOrDefault(checkoutName) is not { } checkout || locked.Checkouts?.GetValueOrDefault(checkoutName)?.Commit is not { } from) return BpmTemplatePlan.None;
        if (from == toCommit) return BpmTemplatePlan.None;
        var bare = BareRepository(projectPath, checkoutName, checkout);
        var folder = CheckoutFolder(projectPath, checkoutName, checkout);
        var changes = await _git.DiffTreesAsync(bare, from, toCommit, ct).ConfigureAwait(false) ?? Array.Empty<GitTreeChange>();
        var prefixes = TemplateFolders(checkout).Select(f => f + "/").ToList();
        var projectFolder = ProjectFolderOf(checkout);
        var plan = new List<BpmTemplateChange>();
        var config = BasisConfigService.TryRead(projectPath);
        foreach (var change in changes)
        {
            if (!prefixes.Any(p => change.Path.StartsWith(p, StringComparison.Ordinal))) continue;
            var relative = projectFolder.Length == 0 ? change.Path : change.Path[(projectFolder.Length + 1)..];
            var local = Path.Combine(projectPath, relative.Replace('/', Path.DirectorySeparatorChar));
            var basePath = Path.Combine(folder, change.Path.Replace('/', Path.DirectorySeparatorChar));
            var localExists = File.Exists(local);
            var same = localExists && File.Exists(basePath) && SameTemplateBytes(local, basePath, relative, config);
            plan.Add(change.Status switch
            {
                'A' => !localExists ? new(relative, BpmTemplateChangeKind.Add, "new in Basis") : new(relative, BpmTemplateChangeKind.Conflict, "new in Basis, and the project already has a different file there"),
                'D' => !localExists ? new(relative, BpmTemplateChangeKind.Skip, "removed in Basis and already gone here") : same ? new(relative, BpmTemplateChangeKind.Delete, "removed in Basis") : new(relative, BpmTemplateChangeKind.Conflict, "removed in Basis, but you changed it"),
                _ => !localExists ? new(relative, BpmTemplateChangeKind.Skip, "changed in Basis, but you deleted it") : same ? new(relative, BpmTemplateChangeKind.Update, "changed in Basis") : new(relative, BpmTemplateChangeKind.Conflict, "changed in Basis and by you"),
            });
        }
        return new BpmTemplatePlan(checkoutName, from, toCommit, plan.OrderBy(c => c.Path, StringComparer.Ordinal).ToList());
    }

    public static BpmResult ApplyTemplate(string projectPath, BpmTemplatePlan plan, string checkoutFolder, string checkoutProjectFolder)
    {
        var done = new List<string>();
        var issues = new List<BpmIssue>();
        int added = 0, updated = 0, deleted = 0;
        var config = BasisConfigService.TryRead(projectPath);
        foreach (var change in plan.Changes)
        {
            var local = Path.Combine(projectPath, change.Path.Replace('/', Path.DirectorySeparatorChar));
            var source = Path.Combine(checkoutFolder, (checkoutProjectFolder.Length == 0 ? change.Path : checkoutProjectFolder + "/" + change.Path).Replace('/', Path.DirectorySeparatorChar));
            try
            {
                switch (change.Kind)
                {
                    case BpmTemplateChangeKind.Add or BpmTemplateChangeKind.Update:
                        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                        File.WriteAllBytes(local, TemplateBytes(change.Path, File.ReadAllBytes(source), config));
                        if (change.Kind == BpmTemplateChangeKind.Add) added++; else updated++;
                        break;
                    case BpmTemplateChangeKind.Delete:
                        File.Delete(local);
                        deleted++;
                        break;
                    case BpmTemplateChangeKind.Conflict:
                        issues.Add(new(BpmIssueLevel.Warning, change.Path, $"{change.Path}: {change.Reason}. Left as it is; compare it with {source}."));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(BpmIssueLevel.Error, change.Path, $"{change.Path}: {ex.Message}")); }
        }
        done.Add($"Template: {added} added, {updated} updated, {deleted} removed, {plan.Changes.Count(c => c.Kind == BpmTemplateChangeKind.Conflict)} left for you.");
        issues.AddRange(ValidateAssets(projectPath, plan.Changes.Where(c => c.Kind is BpmTemplateChangeKind.Add or BpmTemplateChangeKind.Update or BpmTemplateChangeKind.Delete).Select(c => c.Path).ToList()));
        return new BpmResult(!issues.Any(i => i.Level == BpmIssueLevel.Error), issues, done);
    }

    public static IReadOnlyList<BpmIssue> ValidateAssets(string projectPath, IReadOnlyCollection<string>? touched = null)
    {
        var check = touched?.Select(t => Path.GetFullPath(Path.Combine(projectPath, t.EndsWith(".meta", StringComparison.Ordinal) ? t : t + ".meta"))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<BpmIssue>();
        var assets = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assets)) return issues;
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var meta in Directory.EnumerateFiles(assets, "*.meta", SearchOption.AllDirectories))
        {
            var target = meta[..^5];
            if ((check is null || check.Contains(Path.GetFullPath(meta))) && !File.Exists(target) && !Directory.Exists(target)) issues.Add(new(BpmIssueLevel.Warning, Rel(projectPath, meta), $"{Rel(projectPath, meta)} has no asset next to it."));
            string? guid = null;
            try { guid = File.ReadLines(meta).Take(4).Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("guid:", StringComparison.Ordinal))?[5..].Trim(); }
            catch (IOException) { }
            if (string.IsNullOrEmpty(guid)) continue;
            if (seen.TryGetValue(guid, out var other)) issues.Add(new(BpmIssueLevel.Error, Rel(projectPath, meta), $"{Rel(projectPath, meta)} and {Rel(projectPath, other)} share the GUID {guid}."));
            else seen[guid] = meta;
        }
        return issues;
    }

    public static IReadOnlyList<BpmIssue> CompareProjectSettings(string projectPath, string? basisTagManager)
    {
        var issues = new List<BpmIssue>();
        var minePath = Path.Combine(projectPath, "ProjectSettings", "TagManager.asset");
        var mine = File.Exists(minePath) ? ReadTagManager(File.ReadAllLines(minePath)) : null;
        var basis = basisTagManager is null ? null : ReadTagManager(basisTagManager.ReplaceLineEndings().Split(Environment.NewLine));
        if (basis is null) return issues;
        for (var i = 0; i < basis.Value.Layers.Count; i++)
        {
            var wanted = basis.Value.Layers[i];
            if (wanted.Length == 0) continue;
            var have = mine is { } m && i < m.Layers.Count ? m.Layers[i] : "";
            if (have != wanted) issues.Add(new(BpmIssueLevel.Info, "layers", $"Basis expects layer {i} to be \"{wanted}\"" + (have.Length == 0 ? "; it's empty here." : $"; here it's \"{have}\".")));
        }
        foreach (var tag in basis.Value.Tags.Where(t => mine is not { } m || !m.Tags.Contains(t)))
            issues.Add(new(BpmIssueLevel.Info, "tags", $"Basis expects a tag called \"{tag}\"."));
        return issues;
    }

    private static (List<string> Layers, HashSet<string> Tags)? ReadTagManager(IEnumerable<string> lines)
    {
        var layers = new List<string>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        string? section = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("  tags:", StringComparison.Ordinal)) { section = "tags"; if (line.Trim() == "tags: []") section = null; continue; }
            if (line.StartsWith("  layers:", StringComparison.Ordinal)) { section = "layers"; continue; }
            if (!line.StartsWith("  - ", StringComparison.Ordinal)) { if (!line.StartsWith("   ", StringComparison.Ordinal)) section = null; continue; }
            var value = line[4..].Trim();
            if (section == "tags") tags.Add(value);
            else if (section == "layers") layers.Add(value);
        }
        return (layers, tags);
    }

    public async Task<BpmResult> InitAsync(string projectPath, bool followsTemplate, string url, string? rev, IReadOnlyCollection<string>? packages, bool copyTemplate, Action<string>? progress = null, CancellationToken ct = default)
    {
        var issues = new List<BpmIssue>();
        var done = new List<string>();
        if (BpmStore.Exists(projectPath)) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, BpmStore.ManifestFileName, $"{projectPath} already has a package manifest.") }, done);
        Directory.CreateDirectory(projectPath);
        var root = BasisDevRoot(projectPath);
        Directory.CreateDirectory(root);
        var checkout = new BpmCheckout { Url = url };
        var bare = BareRepository(projectPath, DefaultCheckoutName, checkout);
        var ensured = await EnsureBareAsync(bare, url, progress, ct).ConfigureAwait(false);
        if (!ensured.Ok) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, url, ensured.Output) }, done);
        var wanted = string.IsNullOrWhiteSpace(rev) ? BasisInstallService.DefaultBranch : rev.Trim();
        var commit = await FindCommitAsync(bare, wanted, progress, ct).ConfigureAwait(false);
        if (commit is null) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, wanted, $"{wanted} isn't in {url}.") }, done);
        checkout.Commit = commit;
        var index = await IndexFromGitAsync(bare, commit, checkout.PackagesFolder, ct).ConfigureAwait(false);
        var chosen = packages is { Count: > 0 } ? packages.ToList() : followsTemplate ? index.Keys.Order(StringComparer.Ordinal).ToList() : new List<string>();
        var manifest = new BpmManifest
        {
            Settings = followsTemplate ? new BpmSettings { Template = DefaultCheckoutName } : null,
            Checkouts = new Dictionary<string, BpmCheckout>(StringComparer.Ordinal) { [DefaultCheckoutName] = checkout },
        };
        foreach (var id in chosen)
        {
            if (!index.ContainsKey(id)) { issues.Add(new(BpmIssueLevel.Error, id, $"Basis at {Short(commit)} has no package called {id}.")); continue; }
            manifest.Packages[id] = new BpmManifestPackage { Version = "checkout:" + DefaultCheckoutName };
        }
        if (issues.Count > 0) return new BpmResult(false, issues, done);
        BpmStore.Save(projectPath, manifest, new BpmLock());
        done.Add($"Wrote {BpmStore.ManifestFileName}: Basis pinned at {Short(commit)}, {manifest.Packages.Count} package(s).");

        var resolved = await ResolveAsync(projectPath, null, false, progress, ct).ConfigureAwait(false);
        var restored = resolved.Ok ? await RestoreAsync(projectPath, progress, ct).ConfigureAwait(false) : resolved;
        done.AddRange(resolved.Done);
        if (!ReferenceEquals(restored, resolved)) done.AddRange(restored.Done);
        issues.AddRange(resolved.Issues);
        if (!ReferenceEquals(restored, resolved)) issues.AddRange(restored.Issues);
        if (!restored.Ok) return new BpmResult(false, issues, done);

        var folder = CheckoutFolder(projectPath, DefaultCheckoutName, checkout);
        var checkoutProject = Path.Combine(folder, ProjectFolderOf(checkout).Replace('/', Path.DirectorySeparatorChar));
        if (followsTemplate || copyTemplate)
        {
            var lockedNow = BpmStore.ReadLock(projectPath);
            var entry = lockedNow.Checkouts![DefaultCheckoutName];
            if (!followsTemplate)
            {
                var withTemplate = SparseFolders(manifest, lockedNow, DefaultCheckoutName, checkout).Concat(TemplateFolders(checkout)).Distinct(StringComparer.Ordinal).ToList();
                var widened = await RestorePinnedAsync(projectPath, DefaultCheckoutName, checkout, entry, withTemplate, progress, ct).ConfigureAwait(false);
                if (!widened.Ok) issues.Add(new(BpmIssueLevel.Error, DefaultCheckoutName, widened.Output));
            }
            foreach (var part in new[] { "Assets", "ProjectSettings" })
            {
                var from = Path.Combine(checkoutProject, part);
                var to = Path.Combine(projectPath, part);
                if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any()) { issues.Add(new(BpmIssueLevel.Warning, part, $"{part} isn't empty, so the Basis template wasn't copied into it.")); continue; }
                if (Directory.Exists(from)) { CopyFolder(from, to, part); done.Add($"Copied the Basis {part}."); }
            }
            if (!followsTemplate)
            {
                var narrowed = await RestorePinnedAsync(projectPath, DefaultCheckoutName, checkout, entry, SparseFolders(manifest, lockedNow, DefaultCheckoutName, checkout), progress, ct).ConfigureAwait(false);
                if (!narrowed.Ok) issues.Add(new(BpmIssueLevel.Error, DefaultCheckoutName, narrowed.Output));
            }
        }
        else if (Directory.Exists(Path.Combine(projectPath, "ProjectSettings")))
            issues.AddRange(CompareProjectSettings(projectPath, await _git.ShowFileAsync(bare, commit, Join(ProjectFolderOf(checkout), "ProjectSettings/TagManager.asset"), ct).ConfigureAwait(false)));
        if (!followsTemplate && !BasisConfigService.Exists(projectPath))
        {
            var config = BasisConfigService.Starter(projectPath);
            BasisConfigService.Write(projectPath, config);
            done.Add($"Wrote {BasisConfigService.FileName}: {config.ProductName}, {config.Identifier}. Give it an identifier of your own before you ship, so the app doesn't install over another.");
            if (copyTemplate) foreach (var line in BasisConfigService.ApplyToProject(projectPath, false).Done) done.Add(line);
        }

        var packagesManifest = Path.Combine(projectPath, "Packages", "manifest.json");
        if (!File.Exists(packagesManifest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packagesManifest)!);
            var basisManifest = await _git.ShowFileAsync(bare, commit, Join(checkout.PackagesFolder, "manifest.json"), ct).ConfigureAwait(false);
            File.WriteAllText(packagesManifest, RegistryOnly(basisManifest) + "\n");
            done.Add("Created Packages/manifest.json with Basis's registry packages.");
        }
        var mounted = await MountAsync(projectPath, ct).ConfigureAwait(false);
        done.AddRange(mounted.Done);
        issues.AddRange(mounted.Issues);
        try
        {
            var ignoreFiles = BpmIgnore.Files(root);
            foreach (var written in BpmIgnore.Ensure(root)) done.Add($"Added {BasisDevStore.FolderName} to {written}.");
            if (ignoreFiles.Count == 0) issues.Add(new(BpmIssueLevel.Info, BasisDevStore.FolderName, $"No version control was found here. If you add one, ignore {BasisDevStore.FolderName} and commit {BpmStore.ManifestFileName}, {BpmStore.LockFileName} and Packages/manifest.json."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { issues.Add(new(BpmIssueLevel.Warning, BasisDevStore.FolderName, $"Couldn't add {BasisDevStore.FolderName} to the ignore list: {ex.Message}")); }
        return new BpmResult(!issues.Any(i => i.Level == BpmIssueLevel.Error), issues, done);
    }

    public async Task<BpmResult> MovePinAsync(string projectPath, string checkoutName, string rev, Action<string>? progress = null, CancellationToken ct = default)
    {
        var manifest = BpmStore.ReadManifest(projectPath);
        if (manifest.Checkouts?.GetValueOrDefault(checkoutName) is not { } checkout || checkout.Kind != BpmHeld.Pinned || string.IsNullOrWhiteSpace(checkout.Url))
            return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, checkoutName, $"{checkoutName} isn't a pinned checkout in {BpmStore.ManifestFileName}.") }, Array.Empty<string>());
        var bare = BareRepository(projectPath, checkoutName, checkout);
        var ensured = await EnsureBareAsync(bare, checkout.Url, progress, ct).ConfigureAwait(false);
        if (!ensured.Ok) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, checkoutName, ensured.Output) }, Array.Empty<string>());
        progress?.Invoke($"Fetching {checkout.Url}…");
        await _git.FetchRemoteAsync(bare, "origin", progress, ct).ConfigureAwait(false);
        var commit = await FindCommitAsync(bare, rev, progress, ct).ConfigureAwait(false);
        if (commit is null) return new BpmResult(false, new[] { new BpmIssue(BpmIssueLevel.Error, rev, $"{rev} isn't in {checkout.Url}.") }, Array.Empty<string>());
        var from = BpmStore.ReadLock(projectPath).Checkouts?.GetValueOrDefault(checkoutName)?.Commit;
        BpmStore.Update(projectPath, (m, _) => m.Checkouts![checkoutName].Commit = commit);
        var done = new List<string> { $"Moved the {checkoutName} pin from {Short(from)} to {Short(commit)}." };
        if (from is not null && from != commit)
        {
            var changed = await ChangedPackagesAsync(bare, from, commit, checkout.PackagesFolder, ct).ConfigureAwait(false);
            var lockedFolders = BpmStore.ReadLock(projectPath).Packages.Where(p => BpmVersion.Parse(p.Value.Version) is { Kind: BpmVersionKind.Checkout } v && v.Value == checkoutName && p.Value.Folder is not null)
                .ToDictionary(p => p.Value.Folder![(checkout.PackagesFolder.Length + 1)..], p => p.Key, StringComparer.Ordinal);
            var yours = changed.Where(lockedFolders.ContainsKey).Select(f => lockedFolders[f]).Order(StringComparer.Ordinal).ToList();
            done.Add(yours.Count > 0 ? $"Locked packages that changed, so retest them: {string.Join(", ", yours)}." : "None of the locked packages changed.");
            if (changed.Count > yours.Count) done.Add($"{changed.Count - yours.Count} other Basis package(s) changed that this project doesn't use.");
        }
        return new BpmResult(true, Array.Empty<BpmIssue>(), done);
    }

    public async Task<IReadOnlyList<string>> ChangedPackagesAsync(string bare, string from, string to, string packagesFolder, CancellationToken ct = default)
    {
        var changes = await _git.DiffTreesAsync(bare, from, to, ct).ConfigureAwait(false) ?? Array.Empty<GitTreeChange>();
        var prefix = packagesFolder + "/";
        return changes.Where(c => c.Path.StartsWith(prefix, StringComparison.Ordinal) && c.Path.IndexOf('/', prefix.Length) > prefix.Length)
            .Select(c => c.Path[prefix.Length..c.Path.IndexOf('/', prefix.Length)]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private async Task<GitResult> EnsureBareAsync(string bare, string url, Action<string>? progress, CancellationToken ct)
    {
        if (Directory.Exists(bare) && Directory.Exists(Path.Combine(bare, "objects"))) return new GitResult(true, 0, "");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        progress?.Invoke($"Downloading {url}…");
        return await _git.CloneBareAsync(url, bare, blobless: true, progress, ct).ConfigureAwait(false);
    }

    private async Task<string?> FindCommitAsync(string bare, string rev, Action<string>? progress, CancellationToken ct)
    {
        if (!GitUrlPolicy.IsSafeRef(rev)) return null;
        var commit = await _git.ResolveCommitAsync(bare, rev, ct).ConfigureAwait(false);
        if (commit is not null && (BpmVersion.IsCommitHash(rev) || commit == rev)) return commit;
        if (BpmVersion.IsCommitHash(rev) && rev.Length == 40) await _git.FetchCommitAsync(bare, "origin", rev, progress, ct).ConfigureAwait(false);
        else await _git.FetchRemoteAsync(bare, "origin", progress, ct).ConfigureAwait(false);
        return await _git.ResolveCommitAsync(bare, rev, ct).ConfigureAwait(false) ?? commit;
    }

    private async Task<IReadOnlyDictionary<string, BpmPackageInfo>> IndexFromGitAsync(string repo, string commit, string packagesFolder, CancellationToken ct)
    {
        var index = new Dictionary<string, BpmPackageInfo>(StringComparer.OrdinalIgnoreCase);
        var names = await _git.ListTreeNamesAsync(repo, $"{commit}:{packagesFolder}", ct).ConfigureAwait(false) ?? Array.Empty<string>();
        if (await _git.ListTreeBlobsAsync(repo, $"{commit}:{packagesFolder}", ct).ConfigureAwait(false) is { } blobs)
            await _git.PrefetchBlobsAsync(repo, blobs.Where(b => b.Key == "manifest.json" || b.Key.Count(c => c == '/') == 1 && b.Key.EndsWith("/package.json", StringComparison.Ordinal)).Select(b => b.Value.Split(' ')[^1]).ToList(), ct).ConfigureAwait(false);
        foreach (var name in names)
        {
            var folder = Join(packagesFolder, name);
            var text = await _git.ShowFileAsync(repo, commit, folder + "/package.json", ct).ConfigureAwait(false);
            if (text is not null && ParsePackage(text, null, folder) is { } info) index.TryAdd(info.Id, info);
        }
        AddTarballs(index, await _git.ShowFileAsync(repo, commit, Join(packagesFolder, "manifest.json"), ct).ConfigureAwait(false), packagesFolder, names.ToHashSet(StringComparer.Ordinal));
        return index;
    }

    private static IReadOnlyDictionary<string, BpmPackageInfo> IndexFromDisk(string checkoutFolder, string packagesFolder)
    {
        var index = new Dictionary<string, BpmPackageInfo>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(checkoutFolder, packagesFolder.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(root)) return index;
        foreach (var dir in Directory.EnumerateDirectories(root))
            if (ReadPackage(Path.Combine(dir, "package.json"), null, Join(packagesFolder, Path.GetFileName(dir))) is { } info) index.TryAdd(info.Id, info);
        var manifest = Path.Combine(root, "manifest.json");
        AddTarballs(index, File.Exists(manifest) ? File.ReadAllText(manifest) : null, packagesFolder, Directory.EnumerateFiles(root, "*.tgz").Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal));
        return index;
    }

    private static void AddTarballs(Dictionary<string, BpmPackageInfo> index, string? manifestText, string packagesFolder, IReadOnlySet<string> present)
    {
        if (string.IsNullOrWhiteSpace(manifestText)) return;
        try
        {
            using var document = JsonDocument.Parse(manifestText, JsonRead);
            if (!document.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object) return;
            foreach (var p in deps.EnumerateObject())
            {
                if (index.ContainsKey(p.Name) || p.Value.ValueKind != JsonValueKind.String || p.Value.GetString() is not { } value) continue;
                if (!value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || !value.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) continue;
                var file = value[5..];
                if (file.Contains('/') || file.Contains(Path.DirectorySeparatorChar) || !present.Contains(file)) continue;
                index[p.Name] = new BpmPackageInfo(p.Name, Join(packagesFolder, file), null, new Dictionary<string, string>(), new Dictionary<string, string>());
            }
        }
        catch (JsonException) { }
    }

    private static BpmPackageInfo? ReadPackage(string packageJson, string? expectedId, string folder)
    {
        try { return File.Exists(packageJson) ? ParsePackage(File.ReadAllText(packageJson), expectedId, folder) : null; }
        catch (IOException) { return null; }
    }

    public static BpmPackageInfo? ParsePackage(string text, string? expectedId, string folder)
    {
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿'), JsonRead);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var id = root.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : expectedId;
            if (string.IsNullOrWhiteSpace(id)) return null;
            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var needs = Map(root, "bpmDependencies") ?? Map(root, "vpmDependencies") ?? new Dictionary<string, string>(StringComparer.Ordinal);
            var git = Map(root, "gitDependencies") ?? new Dictionary<string, string>(StringComparer.Ordinal);
            return new BpmPackageInfo(id, folder, version, needs, git) { UnityNeeds = Map(root, "dependencies") ?? new Dictionary<string, string>(StringComparer.Ordinal) };
        }
        catch (JsonException) { return null; }

        static Dictionary<string, string>? Map(JsonElement root, string key)
        {
            if (!root.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in value.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.String && BasisDevStore.IsValidId(p.Name)) map[p.Name] = p.Value.GetString() ?? "";
            return map;
        }
    }

    private static PackageManifest? ReadUnityManifest(string path)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (manifest is not null) manifest.Dependencies ??= new Dictionary<string, string>();
            return manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    private static string RegistryOnly(string? manifestText)
    {
        var result = new Dictionary<string, object?>();
        var dependencies = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(manifestText))
        {
            try
            {
                using var document = JsonDocument.Parse(manifestText, JsonRead);
                if (document.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                    foreach (var p in deps.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } value && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) dependencies[p.Name] = value;
                foreach (var p in document.RootElement.EnumerateObject().Where(p => p.Name != "dependencies")) result[p.Name] = p.Value.Clone();
            }
            catch (JsonException) { }
        }
        result["dependencies"] = dependencies;
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void CopyFolder(string from, string to, string? templatePrefix = null, bool skipGit = false)
    {
        bool Skip(string path) => skipGit && Path.GetRelativePath(from, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] == ".git";
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories).Where(d => !Skip(d))) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => !Skip(f)))
        {
            var destination = Path.Combine(to, Path.GetRelativePath(from, file));
            if (File.Exists(destination)) continue;
            if (templatePrefix is null) File.Copy(file, destination);
            else File.WriteAllBytes(destination, TemplateBytes(templatePrefix + "/" + Path.GetRelativePath(from, file).Replace('\\', '/'), File.ReadAllBytes(file)));
        }
    }

    private static readonly (string From, string To)[] ProjectSettingsRenames =
    {
        ("com.BasisUnity.BasisUnity", "org.BasisUnity.BasisUnity"),
        ("com.Basis-Unity.Basis-Unity", "org.Basis-Unity.Basis-Unity"),
    };

    public static byte[] TemplateBytes(string relativePath, byte[] bytes, BasisConfig? config = null)
    {
        if (!string.Equals(relativePath, BasisConfigService.ProjectSettingsFile, StringComparison.Ordinal)) return bytes;
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var renamed = ProjectSettingsRenames.Aggregate(text, (t, r) => t.Replace(r.From, r.To, StringComparison.Ordinal));
        if (config is { HasIdentity: true } && BasisConfigService.Validate(config).All(i => i.Level != BpmIssueLevel.Error)) renamed = BasisConfigService.Apply(renamed, config);
        return renamed == text ? bytes : System.Text.Encoding.UTF8.GetBytes(renamed);
    }

    private static bool SameTemplateBytes(string local, string template, string relativePath, BasisConfig? config)
    {
        var expected = TemplateBytes(relativePath, File.ReadAllBytes(template), config);
        return new FileInfo(local).Length == expected.Length && File.ReadAllBytes(local).AsSpan().SequenceEqual(expected);
    }

    private static string RemoteName(string checkoutName) => new string(checkoutName.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).TrimStart('-', '.') is { Length: > 0 } n ? n : "fork";

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static string Short(string? commit) => string.IsNullOrEmpty(commit) ? "(none)" : commit.Length > 10 ? commit[..10] : commit;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
