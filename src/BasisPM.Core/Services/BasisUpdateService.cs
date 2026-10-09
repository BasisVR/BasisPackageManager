using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisUpdateService
{
    public const string UpstreamRefPrefix = "refs/basispm/upstream/";
    public const string SetAsideRef = "refs/basispm/update-set-aside";
    public const string StateFileName = "basispm-update.json";
    public const string EstimateFileName = "basispm-base.json";
    public const string BranchConfigKey = "basisBranch";
    public const string CommitTrailer = "Basis-Commit";
    public const string BranchTrailer = "Basis-Branch";
    public const string FolderTrailer = "Basis-Folder";
    public const string BasisUnityFolder = "Basis";
    public const double MinimumBaseMatch = 0.5;
    public static readonly Version MinimumGitVersion = new(2, 28, 0);
    public static readonly Version PreviewGitVersion = new(2, 38, 0);
    public static readonly Version MergeBasePreviewGitVersion = new(2, 40, 0);

    private const int CommitPreviewLimit = 200;
    private const int BaseSearchSamples = 48;
    private const int BaseSearchParallelism = 4;
    private const int CandidateBranchLimit = 8;
    private const double LayoutPreference = 0.05;
    private const double ConfidentMatch = 0.98;
    private const string UnityVersionFile = "ProjectSettings/ProjectVersion.txt";
    private const int BasisRefLimit = 64;
    private static readonly TimeSpan HeadsCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly HashSet<string> UnityAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".meta", ".controller", ".anim", ".overrideController", ".physicMaterial",
        ".physicsMaterial2D", ".lighting", ".playable", ".mask", ".renderTexture", ".spriteatlas", ".terrainlayer",
        ".preset", ".shadervariants", ".guiskin", ".fontsettings", ".cubemap", ".flare", ".signal", ".mixer", ".brush",
    };

    private readonly GitService _git;
    private readonly ConcurrentDictionary<string, BasisRecord?> _records = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _headsGate = new(1, 1);
    private IReadOnlyDictionary<string, string>? _heads;
    private DateTime _headsFetchedUtc;

    public BasisUpdateService(GitService git, string upstreamUrl = BasisInstallService.BasisRepoUrl)
    {
        _git = git;
        UpstreamUrl = upstreamUrl;
    }

    public string UpstreamUrl { get; }

    public async Task<IReadOnlyDictionary<string, string>> GetBasisHeadsAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _headsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refresh && _heads is { Count: > 0 } && DateTime.UtcNow - _headsFetchedUtc < HeadsCacheLifetime) return _heads;
            var heads = await _git.ListRemoteHeadsAsync(UpstreamUrl, ct).ConfigureAwait(false);
            if (heads.Count == 0) return _heads ?? heads;
            _heads = heads;
            _headsFetchedUtc = DateTime.UtcNow;
            return heads;
        }
        finally { _headsGate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListBasisBranchesAsync(CancellationToken ct = default) =>
        OrderBranches((await GetBasisHeadsAsync(false, ct).ConfigureAwait(false)).Keys);

    public static IReadOnlyList<string> OrderBranches(IEnumerable<string> names) =>
        names.OrderBy(n => n == BasisInstallService.DefaultBranch ? 0 : IsLongTermSupport(n) ? 1 : 2)
            .ThenByDescending(n => IsLongTermSupport(n) ? n : "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsLongTermSupport(string branch) => branch.StartsWith("long-term-support", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> ResolveRepositoryRootAsync(string projectPath, CancellationToken ct = default)
    {
        if (_git.IsGitRepo(projectPath)) return projectPath;
        var top = await _git.GetTopLevelAsync(projectPath, ct).ConfigureAwait(false);
        if (top is null) return null;
        var relative = Path.GetRelativePath(top, projectPath).Replace('\\', '/');
        return await _git.IsTrackedInHeadAsync(top, relative, ct).ConfigureAwait(false) ? top : null;
    }

    public async Task<string> ResolveBasisBranchAsync(string repoRoot, IReadOnlyDictionary<string, string>? heads = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        return root is null ? BasisInstallService.DefaultBranch : (await FindBasisBranchAsync(root, heads, true, ct).ConfigureAwait(false)).Branch;
    }

    public async Task<(string Branch, BasisBranchSource Source)> DescribeBasisBranchAsync(string repoRoot, IReadOnlyDictionary<string, string>? heads = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        return root is null ? (BasisInstallService.DefaultBranch, BasisBranchSource.Default) : await FindBasisBranchAsync(root, heads, true, ct).ConfigureAwait(false);
    }

    public async Task<BasisBaseInfo?> ResolveBasisBaseAsync(string repoRoot, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null || await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) is null) return null;
        var (branch, _) = await FindBasisBranchAsync(root, null, false, ct).ConfigureAwait(false);
        return await FindBasisBaseAsync(root, branch, null, true, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetBasisDependenciesAsync(string unityProjectPath, CancellationToken ct = default) =>
        (await GetBasisPackagesAsync(unityProjectPath, ct).ConfigureAwait(false)).Dependencies;

    public async Task<BasisPackages> GetBasisPackagesAsync(string unityProjectPath, CancellationToken ct = default)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = await _git.GetTopLevelAsync(unityProjectPath, ct).ConfigureAwait(false);
        if (root is null) return new BasisPackages(ids, null);
        var relative = Path.GetRelativePath(root, unityProjectPath).Replace('\\', '/');
        string basisCommit = "HEAD", manifestPath = BasisManifestPath(BasisLayout.Whole, relative)!;
        string? manifest = null;
        if (await ResolveBasisBaseAsync(root, ct).ConfigureAwait(false) is { Source: BasisBaseSource.Recorded or BasisBaseSource.Similarity } known && BasisManifestPath(known.Layout, relative) is { } knownPath)
        {
            manifest = await _git.ShowFileAsync(root, known.Sha, knownPath, ct).ConfigureAwait(false);
            if (manifest is not null) (basisCommit, manifestPath) = (known.Sha, knownPath);
        }
        if (manifest is null)
        {
            var patterns = new List<string> { UpstreamRefPrefix.TrimEnd('/') };
            foreach (var (name, url) in await _git.ListRemotesAsync(root, ct).ConfigureAwait(false))
                if (IsSameRepository(url, UpstreamUrl)) patterns.Add("refs/remotes/" + name);
            var basisRefs = await _git.ListRefsAsync(root, patterns, BasisRefLimit, ct).ConfigureAwait(false);
            basisCommit = await _git.GetMergeBaseAsync(root, "HEAD", basisRefs, ct).ConfigureAwait(false) ?? "HEAD";
            manifest = await _git.ShowFileAsync(root, basisCommit, manifestPath, ct).ConfigureAwait(false);
        }
        var folders = await _git.ListTreeNamesAsync(root, $"{basisCommit}:{manifestPath[..manifestPath.LastIndexOf('/')]}", ct).ConfigureAwait(false);
        var embedded = folders is null ? null : new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
        if (manifest is null) return new BasisPackages(ids, embedded);
        try
        {
            using var doc = JsonDocument.Parse(manifest);
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var dep in deps.EnumerateObject()) ids.Add(dep.Name);
        }
        catch (JsonException ex) { DiagnosticLog.Write($"Reading the Basis manifest of {unityProjectPath} at {basisCommit}", ex); }
        return new BasisPackages(ids, embedded);
    }

    private static string? BasisManifestPath(BasisLayout layout, string projectRelative)
    {
        var inside = projectRelative == "." ? "" : projectRelative;
        if (layout.ProjectFolder.Length > 0)
        {
            if (!(inside + "/").StartsWith(layout.ProjectFolder + "/", StringComparison.OrdinalIgnoreCase)) return null;
            inside = inside.Length > layout.ProjectFolder.Length ? inside[(layout.ProjectFolder.Length + 1)..] : "";
        }
        var folder = string.Join('/', new[] { layout.BasisFolder, inside }.Where(part => part.Length > 0));
        return folder.Length == 0 ? "Packages/manifest.json" : folder + "/Packages/manifest.json";
    }

    public async Task<BasisStepResult> SetBasisBranchAsync(string repoRoot, string basisBranch, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var local = root is null ? null : await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (root is null || local is null || string.IsNullOrWhiteSpace(basisBranch) || !GitUrlPolicy.IsSafeRef(basisBranch))
            return BasisStepResult.Fail(BasisUpdateFailure.NotApplicable);
        var set = await _git.SetConfigAsync(root, BranchKey(local), basisBranch.Trim(), ct).ConfigureAwait(false);
        return set.Ok ? BasisStepResult.Success : BasisStepResult.Fail(BasisUpdateFailure.NotApplicable, set.Output);
    }

    public async Task<BasisUpdateCheck> CheckAsync(string repoRoot, bool allowFetch = true, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return new BasisUpdateCheck(BasisUpdateStatus.Error, BasisInstallService.DefaultBranch, null, null, false, "Git was not found.");
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return new BasisUpdateCheck(BasisUpdateStatus.NotGitRepo, BasisInstallService.DefaultBranch, null, null, false);
        var state = await LoadStateAsync(root, ct).ConfigureAwait(false);
        if (state is not null)
            return new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, state.Operation == BasisOperation.BranchSwitch ? state.TargetBranch ?? "" : state.BasisBranch,
                state.UpstreamSha, null, true, null, state.Operation);

        var heads = await GetBasisHeadsAsync(false, ct).ConfigureAwait(false);
        var (branch, source) = await FindBasisBranchAsync(root, heads, true, ct).ConfigureAwait(false);
        if (heads.Count == 0) return new BasisUpdateCheck(BasisUpdateStatus.Error, branch, null, null, false, $"Couldn't reach {UpstreamUrl}.");
        if (!heads.TryGetValue(branch, out var tip)) return new BasisUpdateCheck(BasisUpdateStatus.Error, branch, null, null, false, $"The Basis branch '{branch}' was not found.");
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (head is null) return new BasisUpdateCheck(BasisUpdateStatus.NotGitRepo, branch, tip, null, false);

        var have = await _git.ResolveCommitAsync(root, tip, ct).ConfigureAwait(false) is not null;
        if (!have && allowFetch && await ShouldPrefetchAsync(root, branch, ct).ConfigureAwait(false))
        {
            var fetched = await _git.FetchBranchAsync(root, UpstreamUrl, branch, UpstreamRefPrefix + branch, progress, ct).ConfigureAwait(false);
            have = fetched.Ok && await _git.ResolveCommitAsync(root, tip, ct).ConfigureAwait(false) is not null;
        }
        var on = source == BasisBranchSource.Saved ? (await FindBasisBranchAsync(root, heads, false, ct).ConfigureAwait(false)).Branch : branch;
        var basis = await FindBasisBaseAsync(root, on, on == branch && have ? tip : null, true, ct).ConfigureAwait(false);
        if (basis is null) return new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, branch, tip, null, false);
        if (basis.Sha == tip) return new BasisUpdateCheck(BasisUpdateStatus.UpToDate, branch, tip, 0, false);
        int? behind = have && await _git.IsAncestorAsync(root, basis.Sha, tip, ct).ConfigureAwait(false)
            ? await _git.CountCommitsAsync(root, $"{basis.Sha}..{tip}", FolderOrNull(basis.Layout), ct).ConfigureAwait(false)
            : null;
        return new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, branch, tip, behind, false);
    }

    public async Task<BasisUpdatePlan> PlanAsync(string repoRoot, string? basisBranch = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return Blocked(BasisUpdateBlock.GitMissing);
        var version = await _git.GetVersionAsync(ct).ConfigureAwait(false);
        if (version is not null && version < MinimumGitVersion) return Blocked(BasisUpdateBlock.GitTooOld, version.ToString(3));
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return new BasisUpdatePlan { Kind = BasisUpdateKind.NotGitRepo, BasisBranch = basisBranch ?? BasisInstallService.DefaultBranch };

        var state = await LoadStateAsync(root, ct).ConfigureAwait(false);
        if (state is not null)
            return new BasisUpdatePlan { Kind = BasisUpdateKind.InProgress, State = state, BasisBranch = state.BasisBranch, LocalBranch = state.LocalBranch, UpstreamSha = state.UpstreamSha };
        var busy = await _git.GetOperationInProgressAsync(root, ct).ConfigureAwait(false);
        if (busy is not null) return Blocked(BasisUpdateBlock.OperationInProgress, busy);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (head is null) return new BasisUpdatePlan { Kind = BasisUpdateKind.NotGitRepo, BasisBranch = basisBranch ?? BasisInstallService.DefaultBranch };
        var local = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (local is null) return Blocked(BasisUpdateBlock.DetachedHead);

        progress?.Invoke("Checking the Basis branches…");
        var heads = await GetBasisHeadsAsync(true, ct).ConfigureAwait(false);
        if (heads.Count == 0) return Blocked(BasisUpdateBlock.FetchFailed, $"Couldn't reach {UpstreamUrl}.");
        var requested = string.IsNullOrWhiteSpace(basisBranch) ? null : basisBranch.Trim();
        if (requested is not null && (!heads.ContainsKey(requested) || !GitUrlPolicy.IsSafeRef(requested))) return Blocked(BasisUpdateBlock.BranchNotFound, requested, requested);
        var (wanted, wantedSource) = await FindBasisBranchAsync(root, heads, true, ct).ConfigureAwait(false);
        var (on, onSource) = wantedSource == BasisBranchSource.Saved ? await FindBasisBranchAsync(root, heads, false, ct).ConfigureAwait(false) : (wanted, wantedSource);
        var followsOn = requested is null && wantedSource != BasisBranchSource.Saved;
        var target = requested ?? wanted;
        if (!heads.ContainsKey(target)) return Blocked(BasisUpdateBlock.BranchNotFound, target, target);
        var guessing = onSource is BasisBranchSource.History or BasisBranchSource.Estimated or BasisBranchSource.SameName or BasisBranchSource.Default;
        var candidates = guessing ? CandidateBranches(heads) : new List<string>();
        var fetchList = candidates.Append(on).Append(target).Where(heads.ContainsKey).Distinct(StringComparer.Ordinal).ToList();

        progress?.Invoke($"Downloading Basis {target}…");
        var fetch = await _git.FetchBranchesAsync(root, UpstreamUrl, fetchList, UpstreamRefPrefix, progress, ct).ConfigureAwait(false);
        if (!fetch.Ok && fetchList.Count > 1) fetch = await _git.FetchBranchAsync(root, UpstreamUrl, target, UpstreamRefPrefix + target, progress, ct).ConfigureAwait(false);
        if (!fetch.Ok || await _git.ResolveCommitAsync(root, UpstreamRefPrefix + target, ct).ConfigureAwait(false) is null)
            return Blocked(BasisUpdateBlock.FetchFailed, Tail(fetch.Output), target);
        if (guessing && await InferBranchFromHistoryAsync(root, heads, ct).ConfigureAwait(false) is { } inferred)
        {
            on = inferred;
            if (followsOn) target = on;
        }

        var basis = await FindBasisBaseAsync(root, on, await _git.ResolveCommitAsync(root, UpstreamRefPrefix + on, ct).ConfigureAwait(false), false, ct).ConfigureAwait(false);
        if (basis is { Source: BasisBaseSource.Recorded } && await _git.ResolveCommitAsync(root, basis.Sha, ct).ConfigureAwait(false) is null)
        {
            if (heads.ContainsKey(basis.Branch) && !fetchList.Contains(basis.Branch))
                await _git.FetchBranchAsync(root, UpstreamUrl, basis.Branch, UpstreamRefPrefix + basis.Branch, progress, ct).ConfigureAwait(false);
            if (await _git.ResolveCommitAsync(root, basis.Sha, ct).ConfigureAwait(false) is null) basis = null;
        }
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        BasisBaseMatch? match = null;
        if (basis is null)
        {
            if (await _git.IsShallowAsync(root, ct).ConfigureAwait(false)) return Blocked(BasisUpdateBlock.ShallowClone, null, target);
            progress?.Invoke("Finding the Basis version this project started from…");
            var found = await FindBaseAcrossAsync(root, candidates.Count > 0 ? candidates : new List<string> { target }, tracked, progress, ct).ConfigureAwait(false);
            if (found is null || found.Match.MatchRatio < MinimumBaseMatch)
                return new BasisUpdatePlan { Kind = BasisUpdateKind.Unrelated, BasisBranch = target, LocalBranch = local, HeadSha = head };
            match = found.Match;
            on = found.Branch;
            await SaveEstimateAsync(root, local, found, ct).ConfigureAwait(false);
            if (followsOn) target = on;
            basis = new BasisBaseInfo(match.Sha, on, BasisBaseSource.Similarity, false, found.Layout);
        }
        var upstream = await _git.ResolveCommitAsync(root, UpstreamRefPrefix + target, ct).ConfigureAwait(false);
        if (upstream is null) return Blocked(BasisUpdateBlock.FetchFailed, Tail(fetch.Output), target);
        if (basis.Sha == upstream)
            return new BasisUpdatePlan
            {
                Kind = BasisUpdateKind.UpToDate, BasisBranch = target, FromBranch = on, LocalBranch = local, HeadSha = head, UpstreamSha = upstream,
                MergeBase = basis.Sha, Connected = basis.Connected, Layout = basis.Layout, BaseSource = basis.Source, SuggestedBase = match,
            };

        progress?.Invoke("Comparing your project with Basis…");
        var switching = !string.Equals(target, on, StringComparison.Ordinal);
        var gitBase = basis.Connected ? await _git.GetMergeBaseAsync(root, head, upstream, ct).ConfigureAwait(false) : null;
        if (basis.Connected && gitBase == basis.Sha)
        {
            var kind = gitBase == head && !switching ? BasisUpdateKind.FastForward : BasisUpdateKind.Merge;
            var incoming = await _git.GetChangedPathsAsync(root, gitBase, upstream, ct).ConfigureAwait(false);
            var ignoredIncoming = FindIgnoredInTheWay(root, entries, incoming, tracked);
            IReadOnlyList<string>? predicted = kind == BasisUpdateKind.FastForward
                ? Array.Empty<string>()
                : version is not null && version >= PreviewGitVersion ? await _git.PredictConflictsAsync(root, head, upstream, ct).ConfigureAwait(false) : null;
            return new BasisUpdatePlan
            {
                Kind = kind,
                BasisBranch = target,
                FromBranch = on,
                LocalBranch = local,
                HeadSha = head,
                UpstreamSha = upstream,
                MergeBase = gitBase,
                Connected = true,
                BaseSource = basis.Source,
                IncomingCount = await _git.CountCommitsAsync(root, $"{head}..{upstream}", ct).ConfigureAwait(false),
                IncomingCommits = await _git.GetLogAsync(root, $"{head}..{upstream}", CommitPreviewLimit, ct).ConfigureAwait(false),
                LocalCommitCount = await _git.CountCommitsAsync(root, $"{upstream}..{head}", ct).ConfigureAwait(false),
                UncommittedCount = entries.Count,
                CollidingPaths = FindCollisions(entries, incoming),
                BlockingPaths = ignoredIncoming.Blocking,
                ReplacedMetaPaths = ignoredIncoming.Meta,
                PredictedConflicts = predicted,
            };
        }

        var change = await CreateBasisChangeAsync(root, basis.Sha, upstream, basis.Layout, ct).ConfigureAwait(false);
        if (change is null) return Blocked(BasisUpdateBlock.BasisFolderMissing, basis.Layout.BasisFolder, target);
        var changed = await _git.GetChangedPathsAsync(root, change.Value.Parent, change.Value.Commit, ct).ConfigureAwait(false);
        var ignoredChanged = FindIgnoredInTheWay(root, entries, changed, tracked);
        var folder = FolderOrNull(basis.Layout);
        var since = basis.RecordCommit ?? basis.Sha;
        return new BasisUpdatePlan
        {
            Kind = BasisUpdateKind.Apply,
            BasisBranch = target,
            FromBranch = on,
            LocalBranch = local,
            HeadSha = head,
            UpstreamSha = upstream,
            MergeBase = basis.Sha,
            ApplyCommit = change.Value.Commit,
            Connected = basis.Connected,
            Layout = basis.Layout,
            BaseSource = basis.Source,
            SuggestedBase = match,
            IncomingCount = await _git.CountCommitsAsync(root, $"{basis.Sha}..{upstream}", folder, ct).ConfigureAwait(false),
            IncomingCommits = await _git.GetLogAsync(root, $"{basis.Sha}..{upstream}", CommitPreviewLimit, folder, ct).ConfigureAwait(false),
            OutgoingCount = await _git.CountCommitsAsync(root, $"{upstream}..{basis.Sha}", folder, ct).ConfigureAwait(false),
            LocalCommitCount = basis.Source == BasisBaseSource.Similarity ? 0 : await _git.CountCommitsAsync(root, $"{since}..{head}", ct).ConfigureAwait(false),
            UncommittedCount = entries.Count,
            CollidingPaths = FindCollisions(entries, changed),
            BlockingPaths = ignoredChanged.Blocking,
            ReplacedMetaPaths = ignoredChanged.Meta,
            PredictedConflicts = version is not null && version >= MergeBasePreviewGitVersion
                ? await _git.PredictConflictsAsync(root, head, change.Value.Commit, change.Value.Parent, ct).ConfigureAwait(false)
                : null,
        };
    }

    public async Task<BasisBaseMatch?> FindBaseAsync(string repoRoot, string upstreamSha, Action<string>? progress = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return null;
        var history = await _git.GetFirstParentHistoryAsync(root, upstreamSha, ct).ConfigureAwait(false);
        return await SearchHistoryAsync(root, history, BasisLayout.Whole, progress, ct).ConfigureAwait(false);
    }

    private async Task<BasisBaseCandidate?> FindBaseAcrossAsync(string root, IReadOnlyList<string> branches, IReadOnlySet<string> tracked, Action<string>? progress, CancellationToken ct)
    {
        var tips = new List<(string Branch, string Tip)>();
        foreach (var branch in OrderBranches(branches))
            if (await _git.ResolveCommitAsync(root, UpstreamRefPrefix + branch, ct).ConfigureAwait(false) is { } tip) tips.Add((branch, tip));
        if (tips.Count == 0) return null;
        var (primary, primaryTip) = tips[0];
        var history = await _git.GetFirstParentHistoryAsync(root, primaryTip, ct).ConfigureAwait(false);
        BasisBaseCandidate? best = null;
        foreach (var layout in CandidateLayouts(tracked))
        {
            var match = await SearchHistoryAsync(root, history, layout, progress, ct).ConfigureAwait(false);
            if (match is not null && (best is null || match.MatchRatio > best.Match.MatchRatio + LayoutPreference)) best = new BasisBaseCandidate(primary, layout, match);
            if (best is not null && best.Match.MatchRatio >= ConfidentMatch) break;
        }
        if (best is null) return null;
        foreach (var (branch, tip) in tips.Skip(1))
        {
            var only = await _git.GetFirstParentHistoryAsync(root, tip, primaryTip, ct).ConfigureAwait(false);
            var match = only.Count == 0 ? null : await SearchHistoryAsync(root, only, best.Layout, progress, ct).ConfigureAwait(false);
            if (match is not null && match.MatchRatio > best.Match.MatchRatio) best = new BasisBaseCandidate(branch, best.Layout, match);
            else if (best.Branch == primary && await LongTermSupportStartedAtAsync(root, new[] { branch }, best.Match.Sha, new Dictionary<string, string> { [primary] = primaryTip, [branch] = tip }, ct).ConfigureAwait(false) is not null)
                best = best with { Branch = branch };
        }
        return best;
    }

    private async Task<BasisBaseMatch?> SearchHistoryAsync(string root, IReadOnlyList<string> history, BasisLayout layout, Action<string>? progress, CancellationToken ct)
    {
        var project = ProjectTreeish("HEAD", layout);
        var projectFiles = await _git.CountTreeFilesAsync(root, project, ct).ConfigureAwait(false);
        if (history.Count == 0 || projectFiles == 0) return null;

        var scores = new ConcurrentDictionary<int, (double Score, TreeDiffStats Stats)>();
        var compared = 0;
        async Task EvaluateAsync(IEnumerable<int> indices)
        {
            using var gate = new SemaphoreSlim(BaseSearchParallelism);
            await Task.WhenAll(indices.Distinct().Where(i => !scores.ContainsKey(i)).Select(async i =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var stats = await _git.GetTreeDiffStatsAsync(root, BasisTreeish(history[i], layout), project, ct).ConfigureAwait(false);
                    if (stats is null) return;
                    var same = projectFiles - stats.Added - stats.Modified;
                    scores[i] = ((double)same / (projectFiles + stats.Deleted), stats);
                    progress?.Invoke($"Comparing your project with Basis history ({Interlocked.Increment(ref compared)})…");
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }

        var step = Math.Max(1, history.Count / BaseSearchSamples);
        await EvaluateAsync(Enumerable.Range(0, history.Count / step + 1).Select(k => Math.Min(k * step, history.Count - 1)).Append(history.Count - 1)).ConfigureAwait(false);
        var best = BestCandidate(scores);
        while (step > 1 && best >= 0)
        {
            var low = Math.Max(0, best - step);
            var high = Math.Min(history.Count - 1, best + step);
            step = Math.Max(1, step / 8);
            var window = new List<int> { high };
            for (var i = low; i <= high; i += step) window.Add(i);
            await EvaluateAsync(window).ConfigureAwait(false);
            best = BestCandidate(scores);
        }
        if (best < 0) return null;

        var stats = scores[best].Stats;
        var matching = projectFiles - stats.Added - stats.Modified;
        var info = (await _git.GetLogAsync(root, history[best], 1, ct).ConfigureAwait(false)).FirstOrDefault();
        return new BasisBaseMatch(history[best], info?.ShortSha ?? Short(history[best]), info?.Date ?? default, info?.Subject ?? "",
            matching, matching + stats.Deleted + stats.Modified);
    }

    private static int BestCandidate(IReadOnlyDictionary<int, (double Score, TreeDiffStats Stats)> scores) =>
        scores.Count == 0 ? -1 : scores.OrderByDescending(kv => kv.Value.Score).ThenByDescending(kv => kv.Key).First().Key;

    public async Task<BasisStepResult> LinkAsync(string repoRoot, string baseSha, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return BasisStepResult.Fail(BasisUpdateFailure.LinkFailed);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var local = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        var basis = await _git.ResolveCommitAsync(root, baseSha, ct).ConfigureAwait(false);
        if (head is null || local is null || basis is null) return BasisStepResult.Fail(BasisUpdateFailure.LinkFailed);
        if (await _git.GetMergeBaseAsync(root, head, basis, ct).ConfigureAwait(false) is not null) return BasisStepResult.Success;
        var link = await _git.CommitTreeAsync(root, head + "^{tree}", new[] { head, basis }, $"Link project history to Basis {Short(basis)}", ct).ConfigureAwait(false);
        if (link is null) return BasisStepResult.Fail(BasisUpdateFailure.LinkFailed);
        var moved = await _git.UpdateRefAsync(root, "refs/heads/" + local, link, head, $"basispm: link to Basis {Short(basis)}", ct).ConfigureAwait(false);
        return moved.Ok ? BasisStepResult.Success : BasisStepResult.Fail(BasisUpdateFailure.LinkFailed, moved.Output);
    }

    public async Task<BasisStepResult> InitializeRepositoryAsync(string projectRoot, string unityProjectPath, Action<string>? progress = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(projectRoot, ct).ConfigureAwait(false);
        var created = false;
        if (root is null)
        {
            var init = await _git.InitAsync(projectRoot, "main", ct).ConfigureAwait(false);
            if (!init.Ok) return BasisStepResult.Fail(BasisUpdateFailure.InitFailed, init.Output);
            root = projectRoot;
            created = true;
            // Unity projects nest deep folders, and git on Windows refuses paths past 260 characters unless told otherwise.
            if (OperatingSystem.IsWindows()) await _git.SetConfigAsync(root, "core.longpaths", "true", ct).ConfigureAwait(false);
        }
        if (await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) is not null) return BasisStepResult.Success;

        // Before the first commit, Basis's own git rules: its Unity .gitignore keeps the Library cache, builds, logs and IDE
        // files out, and its .gitattributes records text with Basis's line endings so the files line up with Basis's own.
        var added = new List<string>();
        List<string> unhidden, hiddenClones;
        try
        {
            if (await EnsureUnityIgnoreAsync(root, unityProjectPath, ct).ConfigureAwait(false) is { } ignore) added.Add(ignore);
            var attributes = Path.Combine(root, ".gitattributes");
            if (!File.Exists(attributes))
            {
                await File.WriteAllTextAsync(attributes, BasisGitDefaults.Attributes, ct).ConfigureAwait(false);
                added.Add(".gitattributes");
            }
            (unhidden, hiddenClones) = await UnhidePackagesAsync(root, unityProjectPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (created) await BasisInstallService.DeleteFolderAsync(Path.Combine(root, ".git")).ConfigureAwait(false);
            return BasisStepResult.Fail(BasisUpdateFailure.InitFailed, ex.Message);
        }

        var library = Path.Combine(unityProjectPath, "Library");
        if (Directory.Exists(library))
        {
            var relative = Path.GetRelativePath(root, library).Replace('\\', '/');
            if (!await _git.IsIgnoredAsync(root, relative, ct).ConfigureAwait(false))
            {
                if (created) await BasisInstallService.DeleteFolderAsync(Path.Combine(root, ".git")).ConfigureAwait(false);
                return BasisStepResult.Fail(BasisUpdateFailure.LibraryNotIgnored, relative);
            }
        }

        // A folder that's a git clone of its own (a package cloned into Packages, say) would only be recorded as an empty
        // link, so it stays out the way mounted package clones do. Files over GitHub's size limit get a mention.
        var untracked = await _git.ListUntrackedAsync(root, ct).ConfigureAwait(false) ?? Array.Empty<string>();
        var clones = untracked.Where(p => p.EndsWith('/')).Select(p => p.TrimEnd('/')).ToList();
        foreach (var folder in clones) GitExclude.Add(root, Path.Combine(root, folder));
        var large = untracked.Where(p => !p.EndsWith('/') && IsOverGitHubLimit(Path.Combine(root, p))).ToList();

        progress?.Invoke("Recording your project in git… (big projects can take a few minutes)");
        var commit = await _git.CommitAllAsync(root, "Project before first Basis update", ct).ConfigureAwait(false);
        if (!commit.Ok) return BasisStepResult.Fail(BasisUpdateFailure.InitFailed, commit.Output);
        return BasisStepResult.Success with { AddedFiles = added, Unhidden = unhidden, LeftOut = hiddenClones.Concat(clones).ToList(), LargeFiles = large };
    }

    // A Packages/.gitignore from a VRChat project (`/*/`) hides every embedded package, and in a Basis project those are
    // Basis's own, which updates have to reach. Each package folder a project .gitignore hides gets a matching `!` rule
    // appended to that same file. A package that's a git clone of its own stays hidden: it would only be recorded as a link.
    private async Task<(List<string> Unhidden, List<string> Clones)> UnhidePackagesAsync(string root, string unityProjectPath, CancellationToken ct)
    {
        var unhidden = new List<string>();
        var clones = new List<string>();
        var packages = Path.Combine(unityProjectPath, "Packages");
        if (!Directory.Exists(packages)) return (unhidden, clones);
        var folders = Directory.EnumerateDirectories(packages).Where(d => File.Exists(Path.Combine(d, "package.json")))
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/')).ToList();
        foreach (var rules in (await _git.ListIgnoreRulesAsync(root, folders, ct).ConfigureAwait(false)).GroupBy(r => r.Source.Replace('\\', '/')))
        {
            // Only the project's own .gitignore files; .git/info/exclude and global excludes belong to this machine, not the project.
            var source = rules.Key;
            if (Path.IsPathRooted(source) || Path.GetFileName(source) != ".gitignore" || !GitUrlPolicy.IsSafeSubPath(source)) continue;
            var folder = Path.GetDirectoryName(source)?.Replace('\\', '/') ?? "";
            var lines = new List<string>();
            foreach (var (path, _, _) in rules)
            {
                if (Directory.Exists(Path.Combine(root, path, ".git")) || File.Exists(Path.Combine(root, path, ".git"))) { clones.Add(path); continue; }
                var inner = folder.Length == 0 ? path : path[(folder.Length + 1)..];
                lines.Add("!/" + Regex.Replace(inner, @"[\\*?\[]", m => "\\" + m.Value) + "/");
                unhidden.Add(path);
            }
            if (lines.Count == 0) continue;
            var file = Path.Combine(root, source);
            var existing = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var gap = existing.Length == 0 ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
            await File.AppendAllTextAsync(file, gap + "# Packages a Basis project keeps in the project, so git records them (added by Basis Package Manager)\n" + string.Join("\n", lines) + "\n", ct).ConfigureAwait(false);
        }
        // A rule on a parent folder can't be undone from inside it; whatever is still hidden is left for the update to report.
        var stillHidden = (await _git.ListIgnoreRulesAsync(root, unhidden, ct).ConfigureAwait(false)).Select(r => r.Path).ToHashSet(StringComparer.Ordinal);
        return (unhidden.Where(p => !stillHidden.Contains(p)).ToList(), clones);
    }

    // Gives the Unity project Basis's .gitignore, or appends Basis's rules to one that lets any of Unity's generated folders
    // through. Returns the file's path in the repository when it wrote or completed one.
    private async Task<string?> EnsureUnityIgnoreAsync(string root, string unityProjectPath, CancellationToken ct)
    {
        var path = Path.Combine(unityProjectPath, ".gitignore");
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, BasisGitDefaults.UnityIgnore, ct).ConfigureAwait(false);
            return relative;
        }
        foreach (var folder in BasisGitDefaults.GeneratedFolders)
        {
            var full = Path.Combine(unityProjectPath, folder);
            if (!Directory.Exists(full) || await _git.IsIgnoredAsync(root, Path.GetRelativePath(root, full).Replace('\\', '/'), ct).ConfigureAwait(false)) continue;
            var existing = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var gap = existing.Length == 0 ? "" : existing.EndsWith('\n') ? "\n" : "\n\n";
            await File.AppendAllTextAsync(path, gap + "# Unity's generated files, from Basis's own .gitignore (added by Basis Package Manager)\n" + BasisGitDefaults.UnityIgnore, ct).ConfigureAwait(false);
            return relative;
        }
        return null;
    }

    private const long GitHubFileLimit = 100L * 1024 * 1024;

    private static bool IsOverGitHubLimit(string file)
    {
        try { return new FileInfo(file).Length > GitHubFileLimit; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public async Task<BasisUpdateResult> ApplyAsync(string repoRoot, BasisUpdatePlan plan, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (plan.BlockingPaths.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', plan.BlockingPaths));
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var applying = plan.Kind == BasisUpdateKind.Apply;
        if (!plan.CanApply || root is null || plan.HeadSha is null || plan.UpstreamSha is null || plan.MergeBase is null || plan.LocalBranch is null || (applying && plan.ApplyCommit is null))
            return BasisUpdateResult.Fail(BasisUpdateFailure.NotApplicable);
        if (await LoadStateAsync(root, ct).ConfigureAwait(false) is not null) return BasisUpdateResult.Fail(BasisUpdateFailure.AlreadyInProgress);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var local = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (head != plan.HeadSha || local != plan.LocalBranch) return BasisUpdateResult.Fail(BasisUpdateFailure.HeadMoved);

        var incoming = applying
            ? await _git.GetChangedPathsAsync(root, plan.ApplyCommit + "^", plan.ApplyCommit!, ct).ConfigureAwait(false)
            : await _git.GetChangedPathsAsync(root, plan.MergeBase, plan.UpstreamSha, ct).ConfigureAwait(false);
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        var (blockers, discard) = FindIgnoredInTheWay(root, entries, incoming, tracked);
        if (blockers.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', blockers));

        var message = UpdateMessage(plan);
        var state = new BasisUpdateState
        {
            Phase = BasisUpdatePhase.Merging,
            Operation = applying ? BasisOperation.Apply : BasisOperation.Merge,
            BasisBranch = plan.BasisBranch,
            UpstreamSha = plan.UpstreamSha,
            LocalBranch = plan.LocalBranch,
            PreUpdateHead = plan.HeadSha,
            BaseSha = plan.MergeBase,
            BasisParent = applying && plan.Connected && !await _git.IsAncestorAsync(root, plan.UpstreamSha, plan.HeadSha, ct).ConfigureAwait(false) ? plan.UpstreamSha : null,
            CommitMessage = applying ? message : null,
            StartedUtc = DateTimeOffset.UtcNow,
            DiscardedMeta = discard,
        };
        var setAside = await SetAsideAsync(root, state, entries, FindCollisions(entries, incoming), true, $"BasisPM set aside for Basis {Short(plan.UpstreamSha)}", progress, ct).ConfigureAwait(false);
        if (setAside is not null) return setAside;
        await SaveStateAsync(root, state, ct).ConfigureAwait(false);
        await DiscardIgnoredMetaAsync(root, discard, ct).ConfigureAwait(false);

        if (applying)
        {
            progress?.Invoke(plan.IsSwitch ? $"Moving your project to Basis {plan.BasisBranch}…" : "Bringing in the Basis changes…");
            var pick = await _git.CherryPickNoCommitAsync(root, plan.ApplyCommit!, progress, ct).ConfigureAwait(false);
            if (pick.Ok)
            {
                var concluded = await ConcludeApplyAsync(root, state, ct).ConfigureAwait(false);
                return concluded.Ok ? await FinishAsync(root, state, progress, ct).ConfigureAwait(false) : BasisUpdateResult.Fail(BasisUpdateFailure.CommitFailed, concluded.Output);
            }
            var conflicts = await GetConflictsAsync(root, ct).ConfigureAwait(false);
            if (conflicts.Count > 0)
            {
                await _git.SetMergeMessageAsync(root, message, ct).ConfigureAwait(false);
                return BasisUpdateResult.NeedsResolving(BasisUpdatePhase.Merging, conflicts);
            }
            await RollBackApplyAsync(root, ct).ConfigureAwait(false);
            var restored = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
            if (restored.Ok) await RestoreAndClearAsync(root, state, ct).ConfigureAwait(false);
            return BasisUpdateResult.Fail(BasisUpdateFailure.MergeFailed, pick.Output);
        }

        progress?.Invoke("Merging Basis…");
        var merge = await _git.MergeAsync(root, plan.UpstreamSha, message, plan.Kind == BasisUpdateKind.FastForward, progress, ct).ConfigureAwait(false);
        if (merge.Ok) return await FinishAsync(root, state, progress, ct).ConfigureAwait(false);
        if (await _git.IsMergeInProgressAsync(root, ct).ConfigureAwait(false))
            return BasisUpdateResult.NeedsResolving(BasisUpdatePhase.Merging, await GetConflictsAsync(root, ct).ConfigureAwait(false));

        var putBack = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
        if (putBack.Ok) await RestoreAndClearAsync(root, state, ct).ConfigureAwait(false);
        return BasisUpdateResult.Fail(BasisUpdateFailure.MergeFailed, merge.Output);
    }

    public async Task<IReadOnlyList<ProjectBranch>> ListProjectBranchesAsync(string repoRoot, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return Array.Empty<ProjectBranch>();
        var current = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        var remotes = (await _git.ListRemotesAsync(root, ct).ConfigureAwait(false)).Where(r => !IsSameRepository(r.Url, UpstreamUrl)).Select(r => r.Name).ToList();
        var refs = await _git.ListRefTargetsAsync(root, remotes.Select(r => "refs/remotes/" + r).Prepend("refs/heads"), ct).ConfigureAwait(false);
        var locals = refs.Where(r => r.Name.StartsWith("refs/heads/", StringComparison.Ordinal)).Select(r => r.Name["refs/heads/".Length..]).ToHashSet(StringComparer.Ordinal);
        var branches = locals.Select(name => new ProjectBranch(name, null, name == current)).ToList();
        foreach (var (name, _) in refs)
        {
            var remote = remotes.FirstOrDefault(r => name.StartsWith($"refs/remotes/{r}/", StringComparison.Ordinal));
            if (remote is null) continue;
            var branch = name[$"refs/remotes/{remote}/".Length..];
            if (branch != "HEAD" && !locals.Contains(branch)) branches.Add(new ProjectBranch(branch, remote, false));
        }
        return branches.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Remote is not null).ThenBy(b => b.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<BranchSwitchPlan> PlanBranchSwitchAsync(string repoRoot, ProjectBranch target, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return new BranchSwitchPlan { Target = target, Block = BasisUpdateBlock.GitMissing };
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null || string.IsNullOrWhiteSpace(target.Name) || !GitUrlPolicy.IsSafeRef(target.Name) || !GitUrlPolicy.IsSafeRef(target.Remote))
            return new BranchSwitchPlan { Target = target, Block = BasisUpdateBlock.BranchNotFound, Detail = target.Display };
        if (await LoadStateAsync(root, ct).ConfigureAwait(false) is not null) return new BranchSwitchPlan { Target = target, InProgress = true };
        var busy = await _git.GetOperationInProgressAsync(root, ct).ConfigureAwait(false);
        if (busy is not null) return new BranchSwitchPlan { Target = target, Block = BasisUpdateBlock.OperationInProgress, Detail = busy };
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var current = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        var localSha = await _git.ResolveCommitAsync(root, "refs/heads/" + target.Name, ct).ConfigureAwait(false);
        var targetSha = localSha ?? (target.Remote is null ? null : await _git.ResolveCommitAsync(root, $"refs/remotes/{target.Remote}/{target.Name}", ct).ConfigureAwait(false));
        if (head is null || targetSha is null) return new BranchSwitchPlan { Target = target, Block = BasisUpdateBlock.BranchNotFound, Detail = target.Display };
        var incoming = await _git.GetChangedPathsAsync(root, head, targetSha, ct).ConfigureAwait(false);
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        var colliding = FindCollisions(entries, incoming);
        var ignored = FindIgnoredInTheWay(root, entries, incoming, tracked);
        return new BranchSwitchPlan
        {
            Target = localSha is null ? target : target with { Remote = null },
            Block = current is null && colliding.Count > 0 ? BasisUpdateBlock.DetachedHead : BasisUpdateBlock.None,
            CurrentBranch = current,
            HeadSha = head,
            TargetSha = targetSha,
            CreatesBranch = localSha is null,
            UncommittedCount = entries.Count,
            CollidingPaths = colliding,
            BlockingPaths = ignored.Blocking,
            ReplacedMetaPaths = ignored.Meta,
        };
    }

    public async Task<BasisUpdateResult> SwitchBranchAsync(string repoRoot, BranchSwitchPlan plan, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (plan.BlockingPaths.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', plan.BlockingPaths));
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (!plan.CanSwitch || root is null || plan.HeadSha is null || plan.TargetSha is null) return BasisUpdateResult.Fail(BasisUpdateFailure.NotApplicable);
        if (await LoadStateAsync(root, ct).ConfigureAwait(false) is not null) return BasisUpdateResult.Fail(BasisUpdateFailure.AlreadyInProgress);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var current = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (head != plan.HeadSha || current != plan.CurrentBranch) return BasisUpdateResult.Fail(BasisUpdateFailure.HeadMoved);

        var incoming = await _git.GetChangedPathsAsync(root, head, plan.TargetSha, ct).ConfigureAwait(false);
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        var (blockers, discard) = FindIgnoredInTheWay(root, entries, incoming, tracked);
        if (blockers.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', blockers));
        var colliding = FindCollisions(entries, incoming);
        if (current is null && colliding.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.NotApplicable);

        var state = new BasisUpdateState
        {
            Phase = BasisUpdatePhase.Merging,
            Operation = BasisOperation.BranchSwitch,
            UpstreamSha = plan.TargetSha,
            LocalBranch = current ?? "",
            PreUpdateHead = head,
            TargetBranch = plan.Target.Name,
            StartedUtc = DateTimeOffset.UtcNow,
            DiscardedMeta = discard,
        };
        var setAside = await SetAsideAsync(root, state, entries, colliding, false, $"BasisPM set aside to switch to {plan.Target.Name}", progress, ct).ConfigureAwait(false);
        if (setAside is not null) return setAside;
        await SaveStateAsync(root, state, ct).ConfigureAwait(false);
        await DiscardIgnoredMetaAsync(root, discard, ct).ConfigureAwait(false);

        progress?.Invoke($"Switching to {plan.Target.Display}…");
        var switched = await _git.SwitchBranchAsync(root, plan.Target.Name, plan.CreatesBranch && plan.Target.Remote is not null ? plan.Target.Display : null, ct).ConfigureAwait(false);
        if (switched.Ok) return await FinishAsync(root, state, progress, ct).ConfigureAwait(false);
        var putBack = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
        if (putBack.Ok) await RestoreAndClearAsync(root, state, ct).ConfigureAwait(false);
        return BasisUpdateResult.Fail(BasisUpdateFailure.SwitchFailed, switched.Output);
    }

    public async Task<BasisUpdateResult> ContinueAsync(string repoRoot, Action<string>? progress = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var state = root is null ? null : await LoadStateAsync(root, ct).ConfigureAwait(false);
        if (root is null || state is null) return BasisUpdateResult.Fail(BasisUpdateFailure.NoUpdateInProgress);
        var conflicts = await GetConflictsAsync(root, ct).ConfigureAwait(false);
        if (conflicts.Count > 0) return BasisUpdateResult.NeedsResolving(state.Phase, conflicts);

        if (state.Phase == BasisUpdatePhase.Merging && state.Operation == BasisOperation.Apply)
        {
            progress?.Invoke("Recording the update…");
            var concluded = await ConcludeApplyAsync(root, state, ct).ConfigureAwait(false);
            if (!concluded.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.CommitFailed, concluded.Output);
        }
        else if (state.Phase == BasisUpdatePhase.Merging && state.Operation == BasisOperation.Merge)
        {
            if (await _git.IsMergeInProgressAsync(root, ct).ConfigureAwait(false))
            {
                progress?.Invoke("Recording the merge…");
                var commit = await _git.ConcludeMergeAsync(root, ct).ConfigureAwait(false);
                if (!commit.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.CommitFailed, commit.Output);
            }
            else if (!await _git.IsAncestorAsync(root, state.UpstreamSha, "HEAD", ct).ConfigureAwait(false))
            {
                return await AbortAsync(root, ct).ConfigureAwait(false);
            }
        }
        return await FinishAsync(root, state, progress, ct).ConfigureAwait(false);
    }

    public async Task<BasisUpdateResult> AbortAsync(string repoRoot, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var state = root is null ? null : await LoadStateAsync(root, ct).ConfigureAwait(false);
        if (root is null || state is null) return BasisUpdateResult.Fail(BasisUpdateFailure.NoUpdateInProgress);
        var current = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        var switched = state.Operation == BasisOperation.BranchSwitch && current is not null && current == state.TargetBranch && current != state.LocalBranch;
        if ((current ?? "") != state.LocalBranch && !switched)
            return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, $"Switch back to the '{state.LocalBranch}' branch first.");

        if (await _git.IsMergeInProgressAsync(root, ct).ConfigureAwait(false))
        {
            var abort = await _git.AbortMergeAsync(root, ct).ConfigureAwait(false);
            if (!abort.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, abort.Output);
        }
        else if (state is { Operation: BasisOperation.Apply, Phase: BasisUpdatePhase.Merging } && await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) == state.PreUpdateHead)
        {
            var rolledBack = await RollBackApplyAsync(root, ct).ConfigureAwait(false);
            if (!rolledBack.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, rolledBack.Output);
        }
        if (state.StashSha is not null && state.StashApplied)
        {
            await _git.UnstagePathsAsync(root, state.SetAsidePaths, ct).ConfigureAwait(false);
            var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
            var restored = await _git.RestorePathsFromHeadAsync(root, state.SetAsidePaths.Where(tracked.Contains).ToList(), ct).ConfigureAwait(false);
            if (!restored.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, restored.Output);
            foreach (var path in state.SetAsidePaths.Where(p => !tracked.Contains(p) && GitUrlPolicy.IsSafeSubPath(p)))
                TryDeleteFile(Path.Combine(root, path));
            state.StashApplied = false;
            await SaveStateAsync(root, state, ct).ConfigureAwait(false);
        }
        if (switched)
        {
            var back = await _git.SwitchBranchAsync(root, state.LocalBranch, null, ct).ConfigureAwait(false);
            if (!back.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, back.Output);
        }
        else if (await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) != state.PreUpdateHead)
        {
            var reset = await _git.ResetKeepAsync(root, state.PreUpdateHead, ct).ConfigureAwait(false);
            if (!reset.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, reset.Output);
        }
        var putBack = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
        if (!putBack.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.RestoreFailed, putBack.Output);
        await RestoreAndClearAsync(root, state, ct).ConfigureAwait(false);
        return BasisUpdateResult.Aborted(state.PreUpdateHead);
    }

    public async Task<IReadOnlyList<BasisConflict>> GetConflictsAsync(string repoRoot, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return Array.Empty<BasisConflict>();
        var phase = (await LoadStateAsync(root, ct).ConfigureAwait(false))?.Phase ?? BasisUpdatePhase.Merging;
        return (await _git.GetConflictsAsync(root, ct).ConfigureAwait(false)).Select(c => ToConflict(c, phase)).ToList();
    }

    public async Task<BasisStepResult> ResolveAsync(string repoRoot, string path, ConflictChoice choice, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return BasisStepResult.Fail(BasisUpdateFailure.NoUpdateInProgress);
        var phase = (await LoadStateAsync(root, ct).ConfigureAwait(false))?.Phase ?? BasisUpdatePhase.Merging;
        var conflict = (await _git.GetConflictsAsync(root, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Path == path);
        if (conflict is null) return BasisStepResult.Success;

        GitResult result;
        if (choice == ConflictChoice.Resolved)
        {
            var full = Path.Combine(root, path);
            if (File.Exists(full) && HasConflictMarkers(full)) return BasisStepResult.Fail(BasisUpdateFailure.MarkersRemain, path);
            result = File.Exists(full)
                ? await _git.StagePathsAsync(root, new[] { path }, ct).ConfigureAwait(false)
                : await _git.RemovePathsAsync(root, new[] { path }, ct).ConfigureAwait(false);
        }
        else
        {
            var takeOurs = (phase == BasisUpdatePhase.Merging) == (choice == ConflictChoice.Mine);
            result = (takeOurs ? conflict.HasOurs : conflict.HasTheirs)
                ? await _git.TakeConflictSideAsync(root, path, takeOurs, ct).ConfigureAwait(false)
                : await _git.RemovePathsAsync(root, new[] { path }, ct).ConfigureAwait(false);
        }
        return result.Ok ? BasisStepResult.Success : BasisStepResult.Fail(BasisUpdateFailure.ResolveFailed, result.Output);
    }

    private async Task<(string Branch, BasisBranchSource Source)> FindBasisBranchAsync(string root, IReadOnlyDictionary<string, string>? heads, bool includeSaved, CancellationToken ct)
    {
        var local = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (includeSaved && local is not null && await _git.GetConfigValueAsync(root, BranchKey(local), ct).ConfigureAwait(false) is { } saved && GitUrlPolicy.IsSafeRef(saved))
            return (saved, BasisBranchSource.Saved);
        if (await FindRecordAsync(root, ct).ConfigureAwait(false) is { Branch: { } recorded } && (heads is null || heads.ContainsKey(recorded)))
            return (recorded, BasisBranchSource.Recorded);
        if (local is not null)
        {
            var remote = await _git.GetConfigValueAsync(root, $"branch.{local}.remote", ct).ConfigureAwait(false);
            var merge = await _git.GetConfigValueAsync(root, $"branch.{local}.merge", ct).ConfigureAwait(false);
            if (remote is not null && merge is not null && merge.StartsWith("refs/heads/", StringComparison.Ordinal)
                && await _git.GetRemoteUrlAsync(root, remote, ct).ConfigureAwait(false) is { } url && IsSameRepository(url, UpstreamUrl))
                return (merge["refs/heads/".Length..], BasisBranchSource.Tracking);
        }
        if (await InferBranchFromHistoryAsync(root, heads, ct).ConfigureAwait(false) is { } inferred) return (inferred, BasisBranchSource.History);
        if (local is not null && await LoadEstimateAsync(root, local, ct).ConfigureAwait(false) is { } estimate && (heads is null || heads.ContainsKey(estimate.Branch)))
            return (estimate.Branch, BasisBranchSource.Estimated);
        if (local is not null && heads is not null && heads.ContainsKey(local) && local is not ("main" or "master")) return (local, BasisBranchSource.SameName);
        return (BasisInstallService.DefaultBranch, BasisBranchSource.Default);
    }

    private async Task<string?> InferBranchFromHistoryAsync(string root, IReadOnlyDictionary<string, string>? heads, CancellationToken ct)
    {
        var refs = await LocalBasisRefsAsync(root, ct).ConfigureAwait(false);
        if (heads is not null)
            foreach (var branch in CandidateBranches(heads))
                if (await _git.ResolveCommitAsync(root, heads[branch], ct).ConfigureAwait(false) is not null) refs[branch] = heads[branch];
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (branch, sha) in refs)
        {
            if (!IsCandidateBranch(branch) || (heads is not null && !heads.ContainsKey(branch))) continue;
            if (await _git.GetMergeBaseAsync(root, "HEAD", sha, ct).ConfigureAwait(false) is { } mergeBase) bases[branch] = mergeBase;
        }
        if (bases.Count == 0) return null;
        var newest = await _git.IndependentCommitsAsync(root, bases.Values.Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false);
        if (newest.Count != 1) return bases.ContainsKey(BasisInstallService.DefaultBranch) ? BasisInstallService.DefaultBranch : null;
        var winners = bases.Where(kv => kv.Value == newest[0]).Select(kv => kv.Key).ToList();
        if (winners.Count == 1) return winners[0];
        return await LongTermSupportStartedAtAsync(root, winners, newest[0], refs, ct).ConfigureAwait(false)
            ?? (winners.Contains(BasisInstallService.DefaultBranch) ? BasisInstallService.DefaultBranch : OrderBranches(winners)[0]);
    }

    private async Task<string?> LongTermSupportStartedAtAsync(string root, IEnumerable<string> branches, string basisCommit, IReadOnlyDictionary<string, string> tips, CancellationToken ct)
    {
        if (!tips.TryGetValue(BasisInstallService.DefaultBranch, out var developer) || developer == basisCommit) return null;
        foreach (var branch in OrderBranches(branches.Where(IsLongTermSupport)))
            if (tips.TryGetValue(branch, out var tip) && await _git.GetMergeBaseAsync(root, tip, developer, ct).ConfigureAwait(false) == basisCommit) return branch;
        return null;
    }

    // Every ref listed here comes from the Basis repository itself (BasisPM's own fetches, or a remote pointing at Basis), so
    // when a branch shows up more than once the newest wins: a ref left behind by an older fetch must not hide a fresher one.
    // Diverged refs (a force-push between fetches) keep the first listed, BasisPM's own fetch (for-each-ref sorts by name).
    private async Task<Dictionary<string, string>> LocalBasisRefsAsync(string root, CancellationToken ct)
    {
        var remotes = (await _git.ListRemotesAsync(root, ct).ConfigureAwait(false)).Where(r => IsSameRepository(r.Url, UpstreamUrl)).Select(r => $"refs/remotes/{r.Name}/").ToList();
        var refs = await _git.ListRefTargetsAsync(root, remotes.Select(p => p.TrimEnd('/')).Prepend(UpstreamRefPrefix.TrimEnd('/')), ct).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, sha) in refs)
        {
            var prefix = name.StartsWith(UpstreamRefPrefix, StringComparison.Ordinal) ? UpstreamRefPrefix : remotes.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
            if (prefix is null) continue;
            var branch = name[prefix.Length..];
            if (branch.Length == 0 || branch == "HEAD") continue;
            if (!result.TryGetValue(branch, out var seen) || (seen != sha && await _git.IsAncestorAsync(root, seen, sha, ct).ConfigureAwait(false))) result[branch] = sha;
        }
        return result;
    }

    private async Task<BasisBaseInfo?> FindBasisBaseAsync(string root, string branch, string? tip, bool allowEstimate, CancellationToken ct)
    {
        tip ??= (await LocalBasisRefsAsync(root, ct).ConfigureAwait(false)).GetValueOrDefault(branch);
        var mergeBase = tip is null ? null : await _git.GetMergeBaseAsync(root, "HEAD", tip, ct).ConfigureAwait(false);
        var record = await FindRecordAsync(root, ct).ConfigureAwait(false);
        if (record is not null && (mergeBase is null || await _git.IsAncestorAsync(root, mergeBase, record.Commit, ct).ConfigureAwait(false)))
        {
            var connected = mergeBase is not null || await _git.IsAncestorAsync(root, record.BasisSha, "HEAD", ct).ConfigureAwait(false);
            return new BasisBaseInfo(record.BasisSha, record.Branch ?? branch, BasisBaseSource.Recorded, connected, record.Layout, record.Commit);
        }
        if (mergeBase is not null) return new BasisBaseInfo(mergeBase, branch, BasisBaseSource.History, true, BasisLayout.Whole);
        var local = allowEstimate ? await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false) : null;
        return local is null ? null : await LoadEstimateAsync(root, local, ct).ConfigureAwait(false);
    }

    private async Task<BasisBaseInfo?> LoadEstimateAsync(string root, string localBranch, CancellationToken ct)
    {
        var gitDir = await _git.GetGitDirAsync(root, ct).ConfigureAwait(false);
        var path = gitDir is null ? null : Path.Combine(gitDir, EstimateFileName);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            var estimate = await JsonSerializer.DeserializeAsync<BasisBaseEstimate>(fs, JsonOpts, ct).ConfigureAwait(false);
            return estimate is not null && estimate.LocalBranch == localBranch && IsSha(estimate.BasisCommit) && estimate.BasisBranch.Length > 0
                && GitUrlPolicy.IsSafeRef(estimate.BasisBranch) && BasisLayout.Parse(estimate.Layout) is { } layout
                ? new BasisBaseInfo(estimate.BasisCommit, estimate.BasisBranch, BasisBaseSource.Similarity, false, layout)
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading the estimated Basis version of {root}", ex);
            return null;
        }
    }

    private async Task SaveEstimateAsync(string root, string localBranch, BasisBaseCandidate found, CancellationToken ct)
    {
        var gitDir = await _git.GetGitDirAsync(root, ct).ConfigureAwait(false);
        if (gitDir is null) return;
        var estimate = new BasisBaseEstimate { LocalBranch = localBranch, BasisCommit = found.Match.Sha, BasisBranch = found.Branch, Layout = found.Layout.ToString() };
        try { await AtomicFile.WriteAsync(Path.Combine(gitDir, EstimateFileName), stream => JsonSerializer.SerializeAsync(stream, estimate, JsonOpts, ct), ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Saving the estimated Basis version of {root}", ex); }
    }

    private async Task<BasisRecord?> FindRecordAsync(string root, CancellationToken ct)
    {
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (head is null) return null;
        if (_records.TryGetValue(head, out var cached)) return cached;
        var found = await _git.FindLatestCommitMessageAsync(root, "HEAD", $"^{CommitTrailer}: ", ct).ConfigureAwait(false);
        var record = found is { } commit ? ParseRecord(commit.Sha, commit.Message) : null;
        _records[head] = record;
        return record;
    }

    public static BasisRecord? ParseRecord(string commit, string message)
    {
        string? sha = null, branch = null;
        var layout = BasisLayout.Whole;
        foreach (var raw in message.Split('\n'))
        {
            var line = raw.Trim();
            if (TrailerValue(line, CommitTrailer) is { } value && IsSha(value)) sha = value.ToLowerInvariant();
            else if (TrailerValue(line, BranchTrailer) is { Length: > 0 } name && GitUrlPolicy.IsSafeRef(name)) branch = name;
            else if (TrailerValue(line, FolderTrailer) is { } folder && BasisLayout.Parse(folder) is { } parsed) layout = parsed;
        }
        return sha is null ? null : new BasisRecord(commit, sha, branch, layout);
    }

    private static string? TrailerValue(string line, string key) =>
        line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase) ? line[(key.Length + 1)..].Trim() : null;

    private static List<string> CandidateBranches(IReadOnlyDictionary<string, string> heads) =>
        OrderBranches(heads.Keys.Where(IsCandidateBranch)).Take(CandidateBranchLimit + 1).ToList();

    private static bool IsCandidateBranch(string branch) => branch == BasisInstallService.DefaultBranch || IsLongTermSupport(branch);

    private static List<BasisLayout> CandidateLayouts(IEnumerable<string> tracked)
    {
        var layouts = new List<BasisLayout> { BasisLayout.Whole };
        foreach (var path in tracked.OrderBy(p => p.Length).ThenBy(p => p, StringComparer.Ordinal))
        {
            if (path != UnityVersionFile && !path.EndsWith("/" + UnityVersionFile, StringComparison.Ordinal)) continue;
            var layout = new BasisLayout(BasisUnityFolder, path[..^UnityVersionFile.Length].TrimEnd('/'));
            if (!layouts.Contains(layout)) layouts.Add(layout);
            if (layouts.Count > 3) break;
        }
        return layouts;
    }

    private async Task<(string Parent, string Commit)?> CreateBasisChangeAsync(string root, string baseSha, string tipSha, BasisLayout layout, CancellationToken ct)
    {
        var baseTree = await LayoutTreeAsync(root, baseSha, layout, ct).ConfigureAwait(false);
        var tipTree = await LayoutTreeAsync(root, tipSha, layout, ct).ConfigureAwait(false);
        if (baseTree is null || tipTree is null) return null;
        var parent = layout.IsWhole ? baseSha : await _git.CommitTreeAsync(root, baseTree, Array.Empty<string>(), $"Basis {Short(baseSha)}", ct, unsigned: true).ConfigureAwait(false);
        if (parent is null) return null;
        var commit = await _git.CommitTreeAsync(root, tipTree, new[] { parent }, $"Basis {Short(tipSha)}", ct, unsigned: true).ConfigureAwait(false);
        return commit is null ? null : (parent, commit);
    }

    private async Task<string?> LayoutTreeAsync(string root, string commit, BasisLayout layout, CancellationToken ct)
    {
        var tree = await _git.ResolveTreeAsync(root, commit, layout.BasisFolder, ct).ConfigureAwait(false);
        if (tree is null || layout.ProjectFolder.Length == 0) return tree;
        foreach (var segment in layout.ProjectFolder.Split('/').Reverse())
        {
            tree = await _git.MakeTreeAsync(root, new[] { ("040000", "tree", tree, segment) }, ct).ConfigureAwait(false);
            if (tree is null) return null;
        }
        return tree;
    }

    private async Task<GitResult> ConcludeApplyAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (head is null) return new GitResult(false, -1, "The project has no commits.");
        if (head != state.PreUpdateHead) return new GitResult(true, 0, "");
        var tree = await _git.WriteTreeAsync(root, ct).ConfigureAwait(false);
        if (tree is null) return new GitResult(false, -1, "Couldn't record the updated files.");
        var message = state.CommitMessage ?? $"Update Basis to {state.BasisBranch} {Short(state.UpstreamSha)}";
        var parents = state.BasisParent is null ? new[] { head } : new[] { head, state.BasisParent };
        var commit = await _git.CommitTreeAsync(root, tree, parents, message, ct).ConfigureAwait(false);
        if (commit is null) return new GitResult(false, -1, "Couldn't record the update commit.");
        var moved = await _git.UpdateRefAsync(root, "HEAD", commit, head, "basispm: " + message.Split('\n')[0], ct).ConfigureAwait(false);
        if (moved.Ok) await _git.ClearMergeMessageAsync(root, ct).ConfigureAwait(false);
        return moved;
    }

    private async Task<GitResult> RollBackApplyAsync(string root, CancellationToken ct)
    {
        var reset = await _git.ResetMergeAsync(root, ct).ConfigureAwait(false);
        if (!reset.Ok) reset = await _git.ResetIndexToHeadAsync(root, ct).ConfigureAwait(false);
        await _git.ClearMergeMessageAsync(root, ct).ConfigureAwait(false);
        return reset;
    }

    private async Task<BasisUpdateResult?> SetAsideAsync(string root, BasisUpdateState state, IReadOnlyList<GitWorkingEntry> entries, IReadOnlyCollection<string> colliding,
        bool includeStaged, string label, Action<string>? progress, CancellationToken ct)
    {
        // Only edits the update would overwrite leave the checkout, into a stash that's put back afterwards. Any other staged
        // edit just has to leave the index (a merge or an apply needs it to match HEAD): its file stays put, and its exact
        // index entry is recorded and goes back once the update is done or undone.
        var collidingSet = new HashSet<string>(colliding, StringComparer.Ordinal);
        var unstage = includeStaged && entries.Any(e => e.IsStaged);
        var staged = unstage ? await _git.ListStagedEntriesAsync(root, ct).ConfigureAwait(false) : Array.Empty<(string Mode, string Sha, string Path)>();
        if (staged is null) return BasisUpdateResult.Fail(BasisUpdateFailure.SetAsideFailed, "Couldn't read your staged changes.");
        if (collidingSet.Count > 0)
        {
            progress?.Invoke($"Setting aside {collidingSet.Count} edited file(s)…");
            var untracked = entries.Where(e => e.IsUntracked && collidingSet.Contains(e.Path)).Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            var (stash, sha) = await _git.SetAsidePathsAsync(root, collidingSet.Where(p => !untracked.Contains(p)).ToList(), untracked,
                $"{label} ({DateTime.UtcNow:yyyyMMddHHmmssfff})", ct).ConfigureAwait(false);
            if (sha is null) return BasisUpdateResult.Fail(BasisUpdateFailure.SetAsideFailed, stash.Output);
            state.StashSha = sha;
            state.SetAsidePaths = collidingSet.ToList();
            await _git.UpdateRefAsync(root, SetAsideRef, sha, null, "basispm: edits set aside during a Basis update", ct).ConfigureAwait(false);
        }
        if (unstage)
        {
            var unstaged = await _git.UnstageAllAsync(root, ct).ConfigureAwait(false);
            if (!unstaged.Ok)
            {
                if (state.StashSha is not null) await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
                return BasisUpdateResult.Fail(BasisUpdateFailure.SetAsideFailed, unstaged.Output);
            }
            state.StagedEntries = staged.Where(e => !collidingSet.Contains(e.Path)).Select(e => new BasisStagedEntry(e.Mode, e.Sha, e.Path)).ToList();
        }
        return null;
    }

    private static string UpdateMessage(BasisUpdatePlan plan)
    {
        var subject = plan.IsSwitch
            ? $"Switch Basis from {plan.FromBranch} to {plan.BasisBranch} {Short(plan.UpstreamSha!)}"
            : $"Update Basis to {plan.BasisBranch} {Short(plan.UpstreamSha!)}";
        var trailers = $"{BranchTrailer}: {plan.BasisBranch}\n{CommitTrailer}: {plan.UpstreamSha}";
        return plan.Layout.IsWhole ? $"{subject}\n\n{trailers}" : $"{subject}\n\n{trailers}\n{FolderTrailer}: {plan.Layout}";
    }

    private static string BasisTreeish(string commit, BasisLayout layout) => layout.BasisFolder.Length == 0 ? commit : $"{commit}:{layout.BasisFolder}";

    private static string ProjectTreeish(string commit, BasisLayout layout) => layout.ProjectFolder.Length == 0 ? commit : $"{commit}:{layout.ProjectFolder}";

    private static string? FolderOrNull(BasisLayout layout) => layout.BasisFolder.Length == 0 ? null : layout.BasisFolder;

    public async Task<BasisUpdateState?> LoadStateAsync(string repoRoot, CancellationToken ct = default)
    {
        var path = await StatePathAsync(repoRoot, ct).ConfigureAwait(false);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<BasisUpdateState>(fs, JsonOpts, ct).ConfigureAwait(false);
            if (state is null || !IsSha(state.PreUpdateHead) || !IsSha(state.UpstreamSha) || (state.StashSha is not null && !IsSha(state.StashSha))
                || (state.BaseSha is not null && !IsSha(state.BaseSha)) || (state.BasisParent is not null && !IsSha(state.BasisParent)))
            {
                DiagnosticLog.Write($"Ignoring an unreadable Basis update state at {path}", new InvalidDataException(path));
                return null;
            }
            return state;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading the Basis update state for {repoRoot}", ex);
            return null;
        }
    }

    private static bool IsSha(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    public static bool HasConflictMarkers(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var probe = new byte[8000];
            var read = stream.Read(probe, 0, probe.Length);
            if (Array.IndexOf(probe, (byte)0, 0, read) >= 0) return false;
            stream.Position = 0;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                if (line.StartsWith("<<<<<<< ", StringComparison.Ordinal) || line.StartsWith(">>>>>>> ", StringComparison.Ordinal)) return true;
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Checking {file} for conflict markers", ex);
            return false;
        }
    }

    public static bool IsUnityAsset(string path) => UnityAssetExtensions.Contains(Path.GetExtension(path));

    public static bool IsSameRepository(string a, string b) => NormalizeRepositoryUrl(a) == NormalizeRepositoryUrl(b);

    private static string NormalizeRepositoryUrl(string url)
    {
        var u = url.Trim().Replace('\\', '/').TrimEnd('/');
        if (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) u = u[..^4];
        var scp = Regex.Match(u, @"^[^@/]+@([^:/]+):(.+)$");
        if (scp.Success) u = scp.Groups[1].Value + "/" + scp.Groups[2].Value;
        var scheme = u.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) u = u[(scheme + 3)..];
        var at = u.IndexOf('@');
        var slash = u.IndexOf('/');
        if (at >= 0 && (slash < 0 || at < slash)) u = u[(at + 1)..];
        if (u.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) u = u[4..];
        return u.ToLowerInvariant();
    }

    private bool LooksLikeBasis(string url)
    {
        if (IsSameRepository(url, UpstreamUrl)) return true;
        var normalized = NormalizeRepositoryUrl(url);
        return normalized[(normalized.LastIndexOf('/') + 1)..] == "basis";
    }

    private async Task<bool> ShouldPrefetchAsync(string root, string branch, CancellationToken ct)
    {
        if (await _git.ResolveCommitAsync(root, UpstreamRefPrefix + branch, ct).ConfigureAwait(false) is not null) return true;
        return (await _git.ListRemoteUrlsAsync(root, ct).ConfigureAwait(false)).Any(LooksLikeBasis);
    }

    private async Task<BasisUpdateResult> FinishAsync(string root, BasisUpdateState state, Action<string>? progress, CancellationToken ct)
    {
        if (state.StashSha is not null && !state.StashApplied)
        {
            progress?.Invoke("Putting your edits back…");
            state.Phase = BasisUpdatePhase.RestoringChanges;
            await SaveStateAsync(root, state, ct).ConfigureAwait(false);
            var apply = await _git.ApplyStashAsync(root, state.StashSha, ct).ConfigureAwait(false);
            var conflicts = await GetConflictsAsync(root, ct).ConfigureAwait(false);
            if (!apply.Ok && conflicts.Count == 0) return BasisUpdateResult.Fail(BasisUpdateFailure.RestoreFailed, apply.Output);
            state.StashApplied = true;
            await SaveStateAsync(root, state, ct).ConfigureAwait(false);
            if (conflicts.Count > 0) return BasisUpdateResult.NeedsResolving(BasisUpdatePhase.RestoringChanges, conflicts);
        }
        if (state.StashSha is not null)
        {
            await _git.UnstagePathsAsync(root, state.SetAsidePaths, ct).ConfigureAwait(false);
            await DiscardSetAsideAsync(root, state.StashSha, ct).ConfigureAwait(false);
        }
        var restaged = await RestageAsync(root, state, ct).ConfigureAwait(false);
        if (!restaged.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.RestoreFailed, restaged.Output);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        if (state.Operation != BasisOperation.BranchSwitch && state.LocalBranch.Length > 0
            && await FindRecordAsync(root, ct).ConfigureAwait(false) is { } record && record.Commit == head && record.BasisSha == state.UpstreamSha)
        {
            await _git.UnsetConfigAsync(root, BranchKey(state.LocalBranch), ct).ConfigureAwait(false);
            if (await _git.GetGitDirAsync(root, ct).ConfigureAwait(false) is { } gitDir) TryDeleteFile(Path.Combine(gitDir, EstimateFileName));
        }
        await DropDiscardedMetaAsync(root, state, ct).ConfigureAwait(false);
        await ClearStateAsync(root, ct).ConfigureAwait(false);
        return BasisUpdateResult.Updated(head);
    }

    private async Task<GitResult> PutBackSetAsideAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        if (state.StashSha is not null)
        {
            var apply = await _git.ApplyStashAsync(root, state.StashSha, ct).ConfigureAwait(false);
            if (!apply.Ok) return apply;
            await _git.UnstagePathsAsync(root, state.SetAsidePaths, ct).ConfigureAwait(false);
            await DiscardSetAsideAsync(root, state.StashSha, ct).ConfigureAwait(false);
            state.StashSha = null;
            state.StashApplied = false;
            state.SetAsidePaths = new();
        }
        return await RestageAsync(root, state, ct).ConfigureAwait(false);
    }

    // Staged edits the update didn't touch go back into the index exactly as they were. Another git tool can hold
    // index.lock for a moment, so this retries; if it still fails, the update stays paused at putting edits back, where
    // Finish update retries just this and nothing gets recorded twice.
    private async Task<GitResult> RestageAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        if (state.StagedEntries.Count == 0) return new GitResult(true, 0, "");
        var entries = state.StagedEntries.Select(e => (e.Mode, e.Sha, e.Path)).ToList();
        var restaged = await _git.RestageAsync(root, entries, ct).ConfigureAwait(false);
        for (var attempt = 1; !restaged.Ok && attempt < 5; attempt++)
        {
            await Task.Delay(200 * attempt, ct).ConfigureAwait(false);
            restaged = await _git.RestageAsync(root, entries, ct).ConfigureAwait(false);
        }
        if (restaged.Ok)
        {
            state.StagedEntries = new();
            return restaged;
        }
        state.Phase = BasisUpdatePhase.RestoringChanges;
        await SaveStateAsync(root, state, ct).ConfigureAwait(false);
        return restaged;
    }

    private async Task DiscardSetAsideAsync(string root, string stashSha, CancellationToken ct)
    {
        await _git.DropStashAsync(root, stashSha, ct).ConfigureAwait(false);
        await _git.DeleteRefAsync(root, SetAsideRef, ct).ConfigureAwait(false);
    }

    private static BasisConflict ToConflict(GitConflict c, BasisUpdatePhase phase)
    {
        var mine = phase == BasisUpdatePhase.Merging ? c.HasOurs : c.HasTheirs;
        var basis = phase == BasisUpdatePhase.Merging ? c.HasTheirs : c.HasOurs;
        var kind = (mine, basis) switch
        {
            (true, true) => c.HasBase ? ConflictKind.BothChanged : ConflictKind.BothAdded,
            (false, true) => ConflictKind.DeletedByYou,
            (true, false) => ConflictKind.DeletedByBasis,
            _ => ConflictKind.BothDeleted,
        };
        return new BasisConflict(c.Path, kind, IsUnityAsset(c.Path));
    }

    private static IEnumerable<string> PathsOf(GitWorkingEntry entry) =>
        entry.OriginalPath is null ? new[] { entry.Path } : new[] { entry.Path, entry.OriginalPath };

    private static List<string> FindCollisions(IReadOnlyList<GitWorkingEntry> entries, IReadOnlyCollection<string> incoming)
    {
        var incomingSet = new HashSet<string>(incoming, StringComparer.Ordinal);
        return entries.SelectMany(PathsOf).Where(incomingSet.Contains).Distinct(StringComparer.Ordinal).ToList();
    }

    private static (List<string> Blocking, List<string> Meta) FindIgnoredInTheWay(string root, IReadOnlyList<GitWorkingEntry> entries, IReadOnlyCollection<string> incoming, IReadOnlySet<string> tracked)
    {
        var dirty = new HashSet<string>(entries.SelectMany(PathsOf), StringComparer.Ordinal);
        var inTheWay = incoming.Where(p => !tracked.Contains(p) && !dirty.Contains(p) && File.Exists(Path.Combine(root, p))).ToList();
        return (inTheWay.Where(p => !IsUnityMeta(p)).ToList(), inTheWay.Where(IsUnityMeta).ToList());
    }

    private static bool IsUnityMeta(string path) => path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);

    private const string DiscardedMetaFolder = "basispm-discarded";

    private async Task DiscardIgnoredMetaAsync(string root, IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0 || await _git.GetGitDirAsync(root, ct).ConfigureAwait(false) is not { } gitDir) return;
        foreach (var path in paths.Where(GitUrlPolicy.IsSafeSubPath))
        {
            try
            {
                var copy = Path.Combine(gitDir, DiscardedMetaFolder, path);
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                File.Copy(Path.Combine(root, path), copy, overwrite: true);
                File.Delete(Path.Combine(root, path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Setting aside the ignored {path} for Basis's copy", ex); }
        }
    }

    private async Task RestoreAndClearAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        if (state.DiscardedMeta.Count > 0 && await _git.GetGitDirAsync(root, ct).ConfigureAwait(false) is { } gitDir)
        {
            foreach (var path in state.DiscardedMeta.Where(GitUrlPolicy.IsSafeSubPath))
            {
                var copy = Path.Combine(gitDir, DiscardedMetaFolder, path);
                var file = Path.Combine(root, path);
                if (!File.Exists(copy) || File.Exists(file)) continue;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.Copy(copy, file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Putting back the ignored {path}", ex); }
            }
            DeleteDiscardedCopies(gitDir);
        }
        await ClearStateAsync(root, ct).ConfigureAwait(false);
    }

    private async Task DropDiscardedMetaAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        if (state.DiscardedMeta.Count > 0 && await _git.GetGitDirAsync(root, ct).ConfigureAwait(false) is { } gitDir) DeleteDiscardedCopies(gitDir);
    }

    private static void DeleteDiscardedCopies(string gitDir)
    {
        var folder = Path.Combine(gitDir, DiscardedMetaFolder);
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting {folder}", ex); }
    }

    private async Task<string?> StatePathAsync(string repoRoot, CancellationToken ct)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var gitDir = root is null ? null : await _git.GetGitDirAsync(root, ct).ConfigureAwait(false);
        return gitDir is null ? null : Path.Combine(gitDir, StateFileName);
    }

    private async Task SaveStateAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        var path = await StatePathAsync(root, ct).ConfigureAwait(false) ?? throw new InvalidOperationException($"No git directory for {root}.");
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, state, JsonOpts, ct).ConfigureAwait(false);
    }

    private async Task ClearStateAsync(string root, CancellationToken ct)
    {
        var path = await StatePathAsync(root, ct).ConfigureAwait(false);
        if (path is not null) TryDeleteFile(path);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting {path}", ex); }
    }

    private static BasisUpdatePlan Blocked(BasisUpdateBlock block, string? detail = null, string? branch = null) =>
        new() { Kind = BasisUpdateKind.Blocked, Block = block, Detail = detail, BasisBranch = branch ?? "" };

    private static string BranchKey(string local) => $"branch.{local}.{BranchConfigKey}";

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;

    private static string Tail(string text)
    {
        var lines = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length == 0 ? "" : lines[^1];
    }
}
