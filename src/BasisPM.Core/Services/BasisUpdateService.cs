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
    public const string BranchConfigKey = "basisBranch";
    public const double MinimumBaseMatch = 0.5;
    public static readonly Version MinimumGitVersion = new(2, 28, 0);
    public static readonly Version PreviewGitVersion = new(2, 38, 0);

    private const int CommitPreviewLimit = 200;
    private const int BaseSearchSamples = 48;
    private const int BaseSearchParallelism = 4;
    private static readonly TimeSpan HeadsCacheLifetime = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly HashSet<string> UnityAssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".meta", ".controller", ".anim", ".overrideController", ".physicMaterial",
        ".physicsMaterial2D", ".lighting", ".playable", ".mask", ".renderTexture", ".spriteatlas", ".terrainlayer",
        ".preset", ".shadervariants", ".guiskin", ".fontsettings", ".cubemap", ".flare", ".signal", ".mixer", ".brush",
    };

    private readonly GitService _git;
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
        var local = root is null ? null : await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (root is null || local is null) return BasisInstallService.DefaultBranch;
        var configured = await _git.GetConfigValueAsync(root, BranchKey(local), ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        var remote = await _git.GetConfigValueAsync(root, $"branch.{local}.remote", ct).ConfigureAwait(false);
        var merge = await _git.GetConfigValueAsync(root, $"branch.{local}.merge", ct).ConfigureAwait(false);
        if (remote is not null && merge is not null && merge.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            var url = await _git.GetRemoteUrlAsync(root, remote, ct).ConfigureAwait(false);
            if (url is not null && IsSameRepository(url, UpstreamUrl)) return merge["refs/heads/".Length..];
        }
        if (heads is not null && heads.ContainsKey(local) && local is not ("main" or "master")) return local;
        return BasisInstallService.DefaultBranch;
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
        if (state is not null) return new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, state.BasisBranch, state.UpstreamSha, null, true);

        var heads = await GetBasisHeadsAsync(false, ct).ConfigureAwait(false);
        var branch = await ResolveBasisBranchAsync(root, heads, ct).ConfigureAwait(false);
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
        if (!have) return new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, branch, tip, null, false);
        if (await _git.IsAncestorAsync(root, tip, head, ct).ConfigureAwait(false)) return new BasisUpdateCheck(BasisUpdateStatus.UpToDate, branch, tip, 0, false);
        var mergeBase = await _git.GetMergeBaseAsync(root, head, tip, ct).ConfigureAwait(false);
        int? behind = mergeBase is null ? null : await _git.CountCommitsAsync(root, $"{head}..{tip}", ct).ConfigureAwait(false);
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
        var branch = string.IsNullOrWhiteSpace(basisBranch) ? await ResolveBasisBranchAsync(root, heads, ct).ConfigureAwait(false) : basisBranch.Trim();
        if (!heads.ContainsKey(branch)) return Blocked(BasisUpdateBlock.BranchNotFound, branch, branch);

        progress?.Invoke($"Downloading Basis {branch}…");
        var fetch = await _git.FetchBranchAsync(root, UpstreamUrl, branch, UpstreamRefPrefix + branch, progress, ct).ConfigureAwait(false);
        var upstream = fetch.Ok ? await _git.ResolveCommitAsync(root, UpstreamRefPrefix + branch, ct).ConfigureAwait(false) : null;
        if (upstream is null) return Blocked(BasisUpdateBlock.FetchFailed, Tail(fetch.Output), branch);
        if (await _git.IsAncestorAsync(root, upstream, head, ct).ConfigureAwait(false))
            return new BasisUpdatePlan { Kind = BasisUpdateKind.UpToDate, BasisBranch = branch, LocalBranch = local, HeadSha = head, UpstreamSha = upstream };

        var mergeBase = await _git.GetMergeBaseAsync(root, head, upstream, ct).ConfigureAwait(false);
        if (mergeBase is null)
        {
            if (await _git.IsShallowAsync(root, ct).ConfigureAwait(false)) return Blocked(BasisUpdateBlock.ShallowClone, null, branch);
            progress?.Invoke("Finding the Basis version this project started from…");
            var match = await FindBaseAsync(root, upstream, progress, ct).ConfigureAwait(false);
            return new BasisUpdatePlan
            {
                Kind = BasisUpdateKind.Unrelated,
                BasisBranch = branch,
                LocalBranch = local,
                HeadSha = head,
                UpstreamSha = upstream,
                SuggestedBase = match is not null && match.MatchRatio >= MinimumBaseMatch ? match : null,
            };
        }

        progress?.Invoke("Comparing your project with Basis…");
        var kind = mergeBase == head ? BasisUpdateKind.FastForward : BasisUpdateKind.Merge;
        var incoming = await _git.GetChangedPathsAsync(root, mergeBase, upstream, ct).ConfigureAwait(false);
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        IReadOnlyList<string>? predicted = kind == BasisUpdateKind.FastForward
            ? Array.Empty<string>()
            : version is not null && version >= PreviewGitVersion ? await _git.PredictConflictsAsync(root, head, upstream, ct).ConfigureAwait(false) : null;
        return new BasisUpdatePlan
        {
            Kind = kind,
            BasisBranch = branch,
            LocalBranch = local,
            HeadSha = head,
            UpstreamSha = upstream,
            MergeBase = mergeBase,
            IncomingCount = await _git.CountCommitsAsync(root, $"{head}..{upstream}", ct).ConfigureAwait(false),
            IncomingCommits = await _git.GetLogAsync(root, $"{head}..{upstream}", CommitPreviewLimit, ct).ConfigureAwait(false),
            LocalCommitCount = await _git.CountCommitsAsync(root, $"{upstream}..{head}", ct).ConfigureAwait(false),
            UncommittedCount = entries.Count,
            CollidingPaths = FindCollisions(entries, incoming),
            BlockingPaths = FindBlockers(root, entries, incoming, tracked),
            PredictedConflicts = predicted,
        };
    }

    public async Task<BasisBaseMatch?> FindBaseAsync(string repoRoot, string upstreamSha, Action<string>? progress = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (root is null) return null;
        var history = await _git.GetFirstParentHistoryAsync(root, upstreamSha, ct).ConfigureAwait(false);
        var projectFiles = await _git.CountTreeFilesAsync(root, "HEAD", ct).ConfigureAwait(false);
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
                    var stats = await _git.GetTreeDiffStatsAsync(root, history[i], "HEAD", ct).ConfigureAwait(false);
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
        }
        if (await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) is not null) return BasisStepResult.Success;

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
        progress?.Invoke("Recording your project in git…");
        var commit = await _git.CommitAllAsync(root, "Project before first Basis update", ct).ConfigureAwait(false);
        return commit.Ok ? BasisStepResult.Success : BasisStepResult.Fail(BasisUpdateFailure.InitFailed, commit.Output);
    }

    public async Task<BasisUpdateResult> ApplyAsync(string repoRoot, BasisUpdatePlan plan, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (plan.BlockingPaths.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', plan.BlockingPaths));
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        if (!plan.CanApply || root is null || plan.HeadSha is null || plan.UpstreamSha is null || plan.MergeBase is null || plan.LocalBranch is null)
            return BasisUpdateResult.Fail(BasisUpdateFailure.NotApplicable);
        if (await LoadStateAsync(root, ct).ConfigureAwait(false) is not null) return BasisUpdateResult.Fail(BasisUpdateFailure.AlreadyInProgress);
        var head = await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false);
        var local = await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false);
        if (head != plan.HeadSha || local != plan.LocalBranch) return BasisUpdateResult.Fail(BasisUpdateFailure.HeadMoved);

        var incoming = await _git.GetChangedPathsAsync(root, plan.MergeBase, plan.UpstreamSha, ct).ConfigureAwait(false);
        var entries = await _git.GetWorkingEntriesAsync(root, ct).ConfigureAwait(false);
        var tracked = await _git.ListTrackedFilesAsync(root, ct).ConfigureAwait(false);
        var blockers = FindBlockers(root, entries, incoming, tracked);
        if (blockers.Count > 0) return BasisUpdateResult.Fail(BasisUpdateFailure.IgnoredFilesInTheWay, string.Join('\n', blockers));

        var colliding = new HashSet<string>(FindCollisions(entries, incoming), StringComparer.Ordinal);
        var setAside = colliding.Concat(entries.Where(e => e.IsStaged).SelectMany(PathsOf)).Distinct(StringComparer.Ordinal).ToList();
        var state = new BasisUpdateState
        {
            Phase = BasisUpdatePhase.Merging,
            BasisBranch = plan.BasisBranch,
            UpstreamSha = plan.UpstreamSha,
            LocalBranch = plan.LocalBranch,
            PreUpdateHead = plan.HeadSha,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        if (setAside.Count > 0)
        {
            progress?.Invoke($"Setting aside {setAside.Count} edited file(s) that Basis also changes…");
            var untracked = entries.Where(e => e.IsUntracked && colliding.Contains(e.Path)).Select(e => e.Path).ToList();
            var staged = await _git.StagePathsAsync(root, untracked, ct).ConfigureAwait(false);
            if (!staged.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.SetAsideFailed, staged.Output);
            var (stash, sha) = await _git.StashPathsAsync(root, setAside, $"BasisPM set aside for Basis {Short(plan.UpstreamSha)} ({DateTime.UtcNow:yyyyMMddHHmmssfff})", ct).ConfigureAwait(false);
            if (sha is null)
            {
                await _git.UnstagePathsAsync(root, untracked, ct).ConfigureAwait(false);
                return BasisUpdateResult.Fail(BasisUpdateFailure.SetAsideFailed, stash.Output);
            }
            state.StashSha = sha;
            state.SetAsidePaths = setAside;
            await _git.UpdateRefAsync(root, SetAsideRef, sha, null, "basispm: edits set aside during a Basis update", ct).ConfigureAwait(false);
        }
        await SaveStateAsync(root, state, ct).ConfigureAwait(false);

        progress?.Invoke("Merging Basis…");
        var merge = await _git.MergeAsync(root, plan.UpstreamSha, $"Update Basis to {plan.BasisBranch} {Short(plan.UpstreamSha)}", plan.Kind == BasisUpdateKind.FastForward, progress, ct).ConfigureAwait(false);
        if (merge.Ok) return await FinishAsync(root, state, progress, ct).ConfigureAwait(false);
        if (await _git.IsMergeInProgressAsync(root, ct).ConfigureAwait(false))
            return BasisUpdateResult.NeedsResolving(BasisUpdatePhase.Merging, await GetConflictsAsync(root, ct).ConfigureAwait(false));

        var putBack = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
        if (putBack.Ok) await ClearStateAsync(root, ct).ConfigureAwait(false);
        return BasisUpdateResult.Fail(BasisUpdateFailure.MergeFailed, merge.Output);
    }

    public async Task<BasisUpdateResult> ContinueAsync(string repoRoot, Action<string>? progress = null, CancellationToken ct = default)
    {
        var root = await ResolveRepositoryRootAsync(repoRoot, ct).ConfigureAwait(false);
        var state = root is null ? null : await LoadStateAsync(root, ct).ConfigureAwait(false);
        if (root is null || state is null) return BasisUpdateResult.Fail(BasisUpdateFailure.NoUpdateInProgress);
        var conflicts = await GetConflictsAsync(root, ct).ConfigureAwait(false);
        if (conflicts.Count > 0) return BasisUpdateResult.NeedsResolving(state.Phase, conflicts);

        if (state.Phase == BasisUpdatePhase.Merging)
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
        if (await _git.GetCurrentBranchAsync(root, ct).ConfigureAwait(false) != state.LocalBranch)
            return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, $"Switch back to the '{state.LocalBranch}' branch first.");

        if (await _git.IsMergeInProgressAsync(root, ct).ConfigureAwait(false))
        {
            var abort = await _git.AbortMergeAsync(root, ct).ConfigureAwait(false);
            if (!abort.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, abort.Output);
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
        if (await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) != state.PreUpdateHead)
        {
            var reset = await _git.ResetKeepAsync(root, state.PreUpdateHead, ct).ConfigureAwait(false);
            if (!reset.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.AbortFailed, reset.Output);
        }
        if (state.StashSha is not null)
        {
            var putBack = await PutBackSetAsideAsync(root, state, ct).ConfigureAwait(false);
            if (!putBack.Ok) return BasisUpdateResult.Fail(BasisUpdateFailure.RestoreFailed, putBack.Output);
        }
        await ClearStateAsync(root, ct).ConfigureAwait(false);
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

    public async Task<BasisUpdateState?> LoadStateAsync(string repoRoot, CancellationToken ct = default)
    {
        var path = await StatePathAsync(repoRoot, ct).ConfigureAwait(false);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            await using var fs = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<BasisUpdateState>(fs, JsonOpts, ct).ConfigureAwait(false);
            if (state is null || !IsSha(state.PreUpdateHead) || !IsSha(state.UpstreamSha) || (state.StashSha is not null && !IsSha(state.StashSha)))
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
        await ClearStateAsync(root, ct).ConfigureAwait(false);
        return BasisUpdateResult.Updated(await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false));
    }

    private async Task<GitResult> PutBackSetAsideAsync(string root, BasisUpdateState state, CancellationToken ct)
    {
        if (state.StashSha is null) return new GitResult(true, 0, "");
        var apply = await _git.ApplyStashAsync(root, state.StashSha, ct).ConfigureAwait(false);
        if (!apply.Ok) return apply;
        await _git.UnstagePathsAsync(root, state.SetAsidePaths, ct).ConfigureAwait(false);
        await DiscardSetAsideAsync(root, state.StashSha, ct).ConfigureAwait(false);
        return apply;
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

    private static List<string> FindBlockers(string root, IReadOnlyList<GitWorkingEntry> entries, IReadOnlyCollection<string> incoming, IReadOnlySet<string> tracked)
    {
        var dirty = new HashSet<string>(entries.SelectMany(PathsOf), StringComparer.Ordinal);
        return incoming.Where(p => !tracked.Contains(p) && !dirty.Contains(p) && File.Exists(Path.Combine(root, p))).ToList();
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
