using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisContributeService
{
    public const string RefPrefix = "refs/basispm/contribute/";
    public const string DefaultRepository = "BasisVR/Basis";
    private const string GitLinkMode = "160000";
    private const int PushCommitLimit = 2000;
    private const long PushByteLimit = 1_000_000_000;
    private const int MaxDiffLineLength = 2000;

    private readonly GitService _git;
    private readonly BasisUpdateService _updates;
    private readonly GitHubApiService _api;
    private readonly Func<string, string, string, string> _pushUrl;

    public BasisContributeService(GitService git, BasisUpdateService updates, GitHubApiService api, string? repository = null, Func<string, string, string, string>? pushUrl = null)
    {
        _git = git;
        _updates = updates;
        _api = api;
        var slug = repository ?? (UpmGitUrl.Parse(updates.UpstreamUrl) is { IsGitHub: true } parsed ? parsed.Slug : DefaultRepository);
        var parts = slug.Split('/', 2);
        Owner = parts[0];
        Repo = parts.Length > 1 ? parts[1] : parts[0];
        _pushUrl = pushUrl ?? ((owner, repo, token) => $"https://x-access-token:{token}@github.com/{owner}/{repo}.git");
    }

    public string Owner { get; }
    public string Repo { get; }

    public async Task<BasisContributeScan> ScanAsync(string projectPath, string? unityProjectPath = null, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (!_git.IsAvailable) return Blocked(BasisContributeBlock.GitMissing);
        if (await _git.GetVersionAsync(ct).ConfigureAwait(false) is { } version && version < BasisUpdateService.MinimumGitVersion)
            return Blocked(BasisContributeBlock.GitTooOld, version.ToString(3));
        var root = await _updates.ResolveRepositoryRootAsync(projectPath, ct).ConfigureAwait(false);
        if (root is null) return Blocked(BasisContributeBlock.NotGitRepo);
        if (await _git.ResolveCommitAsync(root, "HEAD", ct).ConfigureAwait(false) is null) return Blocked(BasisContributeBlock.NoCommits, null, root);
        if (await _updates.LoadStateAsync(root, ct).ConfigureAwait(false) is not null) return Blocked(BasisContributeBlock.UpdateInProgress, null, root);
        if (await _git.GetOperationInProgressAsync(root, ct).ConfigureAwait(false) is { } busy) return Blocked(BasisContributeBlock.OperationInProgress, busy, root);

        progress?.Invoke("Finding the Basis version this project is based on…");
        var basis = await _updates.ResolveBasisBaseAsync(root, ct).ConfigureAwait(false);
        if (basis is null)
        {
            var plan = await _updates.PlanAsync(root, null, progress, ct).ConfigureAwait(false);
            basis = BaseOf(plan);
            if (basis is null) return Blocked(BasisContributeBlock.BaseUnknown, plan.Kind == BasisUpdateKind.Blocked ? plan.Detail ?? plan.Block.ToString() : null, root);
        }
        if (await _git.ResolveCommitAsync(root, basis.Sha, ct).ConfigureAwait(false) is null)
        {
            progress?.Invoke($"Downloading Basis {basis.Branch}…");
            await _git.FetchBranchAsync(root, _updates.UpstreamUrl, basis.Branch, BasisUpdateService.UpstreamRefPrefix + basis.Branch, progress, ct).ConfigureAwait(false);
            if (await _git.ResolveCommitAsync(root, basis.Sha, ct).ConfigureAwait(false) is null)
                return Blocked(BasisContributeBlock.BaseMissing, basis.Sha, root) with { Base = basis };
        }

        progress?.Invoke("Reading your project's files…");
        var layout = basis.Layout;
        var (snapshot, error) = await _git.SnapshotWorkingTreeAsync(root, layout.ProjectFolder.Length == 0 ? null : layout.ProjectFolder, ct).ConfigureAwait(false);
        if (snapshot is null) return Blocked(BasisContributeBlock.ReadFailed, error, root) with { Base = basis };
        var basisTree = await _git.ResolveTreeAsync(root, basis.Sha, layout.BasisFolder, ct).ConfigureAwait(false);
        if (basisTree is null) return Blocked(BasisContributeBlock.BasisFolderMissing, layout.BasisFolder, root) with { Base = basis };
        var projectTree = await _git.ResolveTreeAsync(root, snapshot, layout.ProjectFolder, ct).ConfigureAwait(false);
        if (projectTree is null) return Blocked(BasisContributeBlock.ReadFailed, layout.ProjectFolder, root) with { Base = basis };

        progress?.Invoke("Comparing your project with Basis…");
        var diff = await _git.DiffTreesAsync(root, basisTree, projectTree, ct).ConfigureAwait(false);
        if (diff is null) return Blocked(BasisContributeBlock.ReadFailed, null, root) with { Base = basis };
        var changes = new List<BasisChange>();
        var skipped = new List<string>();
        foreach (var entry in diff)
        {
            var inProject = Join(layout.ProjectFolder, entry.Path);
            if (entry.OldMode == GitLinkMode || entry.NewMode == GitLinkMode)
            {
                skipped.Add(inProject);
                continue;
            }
            var kind = entry.Status switch { 'A' => BasisChangeKind.Added, 'D' => BasisChangeKind.Deleted, _ => BasisChangeKind.Modified };
            var deleted = kind == BasisChangeKind.Deleted;
            changes.Add(new BasisChange(Join(layout.BasisFolder, entry.Path), inProject, kind, deleted ? null : entry.NewMode, deleted ? null : entry.NewSha));
        }

        var unity = unityProjectPath is null ? null : UnityFolder(root, unityProjectPath, layout);
        var packages = unity is null ? null : await _git.ListTreeNamesAsync(root, $"{basis.Sha}:{Join(unity, "Packages")}", ct).ConfigureAwait(false);
        var inBasis = new HashSet<string>(packages ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var groups = changes
            .GroupBy(c => Classify(c.Path, unity))
            .Select(g => new BasisChangeGroup(g.Key.Area, g.Key.Name, g.Key.Folder, g.Key.Area != BasisChangeArea.Package || inBasis.Contains(g.Key.Name),
                g.OrderBy(c => c.Path, StringComparer.Ordinal).ToList()))
            .OrderBy(g => g.Area)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BasisContributeScan
        {
            RepoRoot = root,
            Base = basis,
            BaseCommit = (await _git.GetLogAsync(root, basis.Sha, 1, ct).ConfigureAwait(false)).FirstOrDefault(),
            UnityFolder = unity,
            BaseTree = basisTree,
            ProjectTree = projectTree,
            Groups = groups,
            Skipped = skipped,
        };
    }

    public async Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(BasisContributeScan scan, BasisChange change, int maxLines = 5000, CancellationToken ct = default)
    {
        if (scan.Base is null || scan.BaseTree is null || scan.ProjectTree is null) return new[] { new BasisDiffLine(BasisDiffLineKind.Note, "This project hasn't been compared with Basis yet.") };
        var folder = scan.Base.Layout.BasisFolder;
        var path = folder.Length == 0 ? change.Path : change.Path.StartsWith(folder + "/", StringComparison.Ordinal) ? change.Path[(folder.Length + 1)..] : null;
        var text = path is null ? null : await _git.DiffPathAsync(scan.RepoRoot, scan.BaseTree, scan.ProjectTree, path, ct).ConfigureAwait(false);
        return text is null ? new[] { new BasisDiffLine(BasisDiffLineKind.Note, "Couldn't show the changes for this file.") } : ParseDiff(text, maxLines);
    }

    public static IReadOnlyList<BasisDiffLine> ParseDiff(string text, int maxLines = 5000)
    {
        var lines = new List<BasisDiffLine>();
        var inHunk = false;
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var count = raw.Length > 0 && raw[^1].Length == 0 ? raw.Length - 1 : raw.Length;
        for (var i = 0; i < count; i++)
        {
            if (lines.Count >= maxLines)
            {
                lines.Add(new BasisDiffLine(BasisDiffLineKind.Note, $"… {count - i} more lines not shown."));
                break;
            }
            var line = raw[i].Length > MaxDiffLineLength ? $"{raw[i][..MaxDiffLineLength]} … {raw[i].Length - MaxDiffLineLength} more characters" : raw[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                lines.Add(new BasisDiffLine(BasisDiffLineKind.Hunk, line));
            }
            else if (!inHunk || line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                inHunk = false;
                if (!IsDiffPreamble(line))
                    lines.Add(new BasisDiffLine(line.StartsWith("Binary files ", StringComparison.Ordinal) ? BasisDiffLineKind.Note : BasisDiffLineKind.Header, line));
            }
            else if (line.StartsWith('+')) lines.Add(new BasisDiffLine(BasisDiffLineKind.Added, line));
            else if (line.StartsWith('-')) lines.Add(new BasisDiffLine(BasisDiffLineKind.Removed, line));
            else lines.Add(new BasisDiffLine(line.StartsWith('\\') ? BasisDiffLineKind.Note : BasisDiffLineKind.Context, line));
        }
        if (lines.Count == 0) lines.Add(new BasisDiffLine(BasisDiffLineKind.Note, "Only the file mode changed."));
        return lines;
    }

    private static bool IsDiffPreamble(string line) =>
        line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
        || line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal);

    public static IReadOnlyList<string> SuggestSelection(BasisContributeScan scan, string? packageId = null) =>
        scan.Groups
            .Where(g => g.Area == BasisChangeArea.Package && (packageId is null ? g.InBasis : string.Equals(g.Name, packageId, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(g => g.Changes)
            .Where(c => !IsEditorChurn(scan, c))
            .Select(c => c.Path)
            .ToList();

    public static bool IsEditorChurn(BasisContributeScan scan, BasisChange change)
    {
        if (change.Kind != BasisChangeKind.Modified) return false;
        var unity = scan.UnityFolder is { Length: > 0 } folder ? folder + "/" : "";
        var inUnity = change.Path.StartsWith(unity, StringComparison.Ordinal) ? change.Path[unity.Length..] : null;
        if (inUnity is "Packages/packages-lock.json" or "ProjectSettings/ProjectVersion.txt" or "Assets/AddressableAssetsData/AddressableAssetSettings.asset") return true;
        return change.Path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase) && change.Path.Contains("/Fonts/", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<BasisChange> ExpandSelection(BasisContributeScan scan, IEnumerable<string> paths)
    {
        var all = new Dictionary<string, BasisChange>(StringComparer.Ordinal);
        foreach (var change in scan.Changes) all[change.Path] = change;
        var picked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!all.ContainsKey(path)) continue;
            picked.Add(path);
            if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                if (all.ContainsKey(path[..^5])) picked.Add(path[..^5]);
            }
            else if (all.ContainsKey(path + ".meta")) picked.Add(path + ".meta");
            for (var folder = Parent(path); folder.Length > 0; folder = Parent(folder))
                if (all.TryGetValue(folder + ".meta", out var meta) && meta.Kind == BasisChangeKind.Added) picked.Add(meta.Path);
        }
        foreach (var meta in all.Values.Where(c => c.IsMeta && c.Kind == BasisChangeKind.Deleted).OrderByDescending(c => c.Path.Length))
        {
            if (picked.Contains(meta.Path)) continue;
            var folder = meta.Path[..^5] + "/";
            var inside = all.Keys.Where(p => p.StartsWith(folder, StringComparison.Ordinal)).ToList();
            if (inside.Count > 0 && inside.All(picked.Contains)) picked.Add(meta.Path);
        }
        return all.Values.Where(c => picked.Contains(c.Path)).OrderBy(c => c.Path, StringComparer.Ordinal).ToList();
    }

    public static string SuggestBranch(string? name, DateTimeOffset now)
    {
        var slug = new string((name ?? "").ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' ? c : '-').ToArray());
        while (slug.Contains("--", StringComparison.Ordinal)) slug = slug.Replace("--", "-", StringComparison.Ordinal);
        slug = slug.Trim('-', '.');
        if (slug.Length > 40) slug = slug[..40].Trim('-', '.');
        return $"basispm/{(slug.Length == 0 ? "changes" : slug)}-{now:yyyyMMdd-HHmm}";
    }

    public static string SuggestTarget(BasisContributeScan scan) =>
        scan.Base is { Branch: { Length: > 0 } branch } && !branch.StartsWith("long-term-support", StringComparison.OrdinalIgnoreCase)
            ? branch
            : BasisInstallService.DefaultBranch;

    public async Task<BasisContributeCommit> CreateCommitAsync(BasisContributeScan scan, IEnumerable<string> paths, string message, string authorName, string authorEmail, CancellationToken ct = default)
    {
        if (scan.IsBlocked || scan.Base is null) return BasisContributeCommit.Fail("This project couldn't be compared with Basis.");
        var selected = ExpandSelection(scan, paths);
        if (selected.Count == 0) return BasisContributeCommit.Fail("Pick at least one change to send.");
        var files = selected.Where(c => c.Kind != BasisChangeKind.Deleted && c.Mode is not null && c.Sha is not null).Select(c => (c.Mode!, c.Sha!, c.Path)).ToList();
        var deletions = selected.Where(c => c.Kind == BasisChangeKind.Deleted).Select(c => c.Path).ToList();
        var tree = await _git.BuildTreeAsync(scan.RepoRoot, scan.Base.Sha, files, deletions, ct).ConfigureAwait(false);
        if (tree is null) return BasisContributeCommit.Fail("Couldn't put the selected files together. Scan the project again and retry.");
        if (tree == await _git.ResolveTreeAsync(scan.RepoRoot, scan.Base.Sha, "", ct).ConfigureAwait(false))
            return BasisContributeCommit.Fail("The selected files already match Basis.");
        var commit = await _git.CommitTreeAsAsync(scan.RepoRoot, tree, scan.Base.Sha, message, authorName, authorEmail, ct).ConfigureAwait(false);
        return commit is null
            ? BasisContributeCommit.Fail("Couldn't create the commit for the pull request.")
            : new BasisContributeCommit(true, commit, selected.Count, null);
    }

    public async Task<BasisContributeResult> SubmitAsync(BasisContributeScan scan, IEnumerable<string> paths, BasisPullRequestDraft draft, string token, GitHubUser user, Action<string>? progress = null, CancellationToken ct = default)
    {
        if (scan.IsBlocked || scan.Base is null) return BasisContributeResult.Fail("This project couldn't be compared with Basis.");
        var root = scan.RepoRoot;
        var title = draft.Title.Trim();
        var branch = draft.Branch.Trim();
        var target = draft.TargetBranch.Trim();
        if (title.Length == 0) return BasisContributeResult.Fail("Give the pull request a title.");
        if (!await _git.IsValidBranchNameAsync(root, branch, ct).ConfigureAwait(false)) return BasisContributeResult.Fail($"'{branch}' isn't a valid branch name.");
        if (!await _git.IsValidBranchNameAsync(root, target, ct).ConfigureAwait(false)) return BasisContributeResult.Fail($"'{target}' isn't a valid branch name.");
        void Report(string line) => progress?.Invoke(Scrub(line, token));

        Report($"Checking {Owner}/{Repo} on GitHub…");
        var upstream = await _api.GetRepoAsync(token, Owner, Repo, ct).ConfigureAwait(false);
        if (upstream is null) return BasisContributeResult.Fail($"Couldn't look up {Owner}/{Repo} on GitHub. Check your connection and sign-in, then try again.");

        Report("Creating the commit…");
        var author = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name!;
        var commit = await CreateCommitAsync(scan, paths, ComposeMessage(draft), author, user.NoReplyEmail, ct).ConfigureAwait(false);
        if (!commit.Ok || commit.Commit is null) return BasisContributeResult.Fail(commit.Error ?? "Couldn't create the commit for the pull request.");

        var pushOwner = Owner;
        var pushRepo = Repo;
        var forked = false;
        if (!upstream.CanPush)
        {
            Report($"Forking {Owner}/{Repo}…");
            GitHubRepo fork;
            try { fork = await _api.ForkRepoAsync(token, Owner, Repo, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
            {
                DiagnosticLog.Write($"Forking {Owner}/{Repo}", ex);
                return BasisContributeResult.Fail(Scrub(ex.Message, token), commit.Commit);
            }
            pushOwner = fork.Owner?.Login is { Length: > 0 } login ? login : user.Login;
            pushRepo = fork.Name.Length > 0 ? fork.Name : Repo;
            forked = true;
            await WaitForForkAsync(token, pushOwner, pushRepo, Report, ct).ConfigureAwait(false);
            Report($"Updating {target} on {pushOwner}/{pushRepo}…");
            await _api.SyncForkAsync(token, pushOwner, pushRepo, target, ct).ConfigureAwait(false);
        }

        Report($"Downloading Basis {target}…");
        await _git.FetchBranchAsync(root, _updates.UpstreamUrl, target, BasisUpdateService.UpstreamRefPrefix + target, Report, ct).ConfigureAwait(false);
        var url = _pushUrl(pushOwner, pushRepo, token);
        var remote = await _git.ListRemoteRefsAsync(root, url, ct).ConfigureAwait(false);
        if (remote is null) return BasisContributeResult.Fail($"Couldn't reach {pushOwner}/{pushRepo} to push. Check your connection and sign-in, then try again.", commit.Commit);
        var estimate = await _git.EstimatePushAsync(root, commit.Commit, remote.Values, ct).ConfigureAwait(false);
        if (estimate is null || estimate.Commits > PushCommitLimit || estimate.Bytes > PushByteLimit)
            return BasisContributeResult.Fail(TooLargeMessage(estimate, pushOwner, pushRepo, target, forked), commit.Commit);

        var previous = await _git.ResolveCommitAsync(root, RefPrefix + branch, ct).ConfigureAwait(false);
        string? lease = null;
        if (remote.TryGetValue("refs/heads/" + branch, out var existing))
        {
            if (previous is null || !string.Equals(existing, previous, StringComparison.OrdinalIgnoreCase))
                return BasisContributeResult.Fail($"{pushOwner}/{pushRepo} already has a branch named {branch}. Pick another branch name.", commit.Commit);
            lease = existing;
        }

        Report($"Pushing {branch} to {pushOwner}/{pushRepo}…");
        var push = await _git.PushCommitAsync(root, url, commit.Commit, branch, lease, Report, ct).ConfigureAwait(false);
        if (!push.Ok) return BasisContributeResult.Fail($"Push failed: {Scrub(push.Output, token)}", commit.Commit);
        await _git.UpdateRefAsync(root, RefPrefix + branch, commit.Commit, null, "basispm: send changes to Basis", ct).ConfigureAwait(false);

        Report("Opening the pull request…");
        if (await _api.FindOpenPullRequestAsync(token, Owner, Repo, $"{pushOwner}:{branch}", ct).ConfigureAwait(false) is { } open)
            return BasisContributeResult.Success(open.HtmlUrl, commit.Commit, forked, updated: true);
        try
        {
            var head = forked ? $"{pushOwner}:{branch}" : branch;
            var pr = await _api.CreatePullRequestAsync(token, Owner, Repo, title, head, target, ComposeBody(draft, scan), ct).ConfigureAwait(false);
            return BasisContributeResult.Success(pr.HtmlUrl, commit.Commit, forked, updated: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
        {
            DiagnosticLog.Write($"Opening a pull request on {Owner}/{Repo}", ex);
            return BasisContributeResult.Fail($"Pushed {branch}, but GitHub didn't open the pull request: {Scrub(ex.Message, token)}", commit.Commit,
                $"https://github.com/{Owner}/{Repo}/compare/{target}...{pushOwner}:{branch}?expand=1");
        }
    }

    public static string ComposeMessage(BasisPullRequestDraft draft) =>
        string.IsNullOrWhiteSpace(draft.Body) ? draft.Title.Trim() : draft.Title.Trim() + "\n\n" + draft.Body.Trim();

    private static string ComposeBody(BasisPullRequestDraft draft, BasisContributeScan scan)
    {
        var based = $"Based on Basis {Short(scan.Base!.Sha)} ({scan.Base.Branch}).";
        return string.IsNullOrWhiteSpace(draft.Body) ? based : draft.Body.Trim() + "\n\n" + based;
    }

    private static string TooLargeMessage(GitPushEstimate? estimate, string owner, string repo, string target, bool forked)
    {
        var size = estimate?.Bytes is { } bytes ? $" ({bytes / (1024 * 1024)} MB)" : "";
        var fix = forked
            ? $"Bring {target} on {owner}/{repo} up to date with Basis (Sync fork on GitHub), then try again."
            : $"Update this project from Basis {target} first, then try again.";
        return estimate is null
            ? $"Couldn't work out how much would be uploaded to {owner}/{repo}. {fix}"
            : $"Pushing would upload {estimate.Commits} commits{size} of Basis history that {owner}/{repo} doesn't have. {fix}";
    }

    private async Task WaitForForkAsync(string token, string owner, string repo, Action<string> report, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (await _api.GetRepoAsync(token, owner, repo, ct).ConfigureAwait(false) is not null) return;
            report("Waiting for the fork to be ready…");
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
    }

    private static BasisBaseInfo? BaseOf(BasisUpdatePlan plan) =>
        plan.MergeBase is { Length: > 0 } sha && plan.Kind is BasisUpdateKind.UpToDate or BasisUpdateKind.FastForward or BasisUpdateKind.Merge or BasisUpdateKind.Apply
            ? new BasisBaseInfo(sha, plan.FromBranch ?? plan.BasisBranch, plan.BaseSource, plan.Connected, plan.Layout)
            : null;

    private static (BasisChangeArea Area, string Name, string Folder) Classify(string path, string? unity)
    {
        if (unity is null) return (BasisChangeArea.Repository, "", "");
        var prefix = unity.Length == 0 ? "" : unity + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return (BasisChangeArea.Repository, "", "");
        var parts = path[prefix.Length..].Split('/');
        return parts.Length >= 3 && parts[0] == "Packages"
            ? (BasisChangeArea.Package, parts[1], prefix + "Packages/" + parts[1])
            : (BasisChangeArea.Project, "", unity);
    }

    private static string? UnityFolder(string root, string unityProjectPath, BasisLayout layout)
    {
        var relative = Path.GetRelativePath(root, unityProjectPath).Replace('\\', '/').Trim('/');
        if (relative == ".") relative = "";
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return null;
        if (layout.ProjectFolder.Length > 0)
        {
            if (relative != layout.ProjectFolder && !relative.StartsWith(layout.ProjectFolder + "/", StringComparison.Ordinal)) return null;
            relative = relative.Length > layout.ProjectFolder.Length ? relative[(layout.ProjectFolder.Length + 1)..] : "";
        }
        return Join(layout.BasisFolder, relative);
    }

    private static BasisContributeScan Blocked(BasisContributeBlock block, string? detail = null, string root = "") =>
        new() { Block = block, Detail = detail, RepoRoot = root };

    private static string Join(string folder, string path) => folder.Length == 0 ? path : path.Length == 0 ? folder : folder + "/" + path;

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;

    private static string Scrub(string text, string token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***", StringComparison.Ordinal);
}
