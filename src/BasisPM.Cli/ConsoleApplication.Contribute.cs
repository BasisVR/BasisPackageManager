using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private async Task ContributeAsync(List<string> values)
    {
        var options = values.ToList();
        var packages = TakeValues(options, "--package");
        var paths = TakeValues(options, "--path");
        var projectFiles = TakeFlag(options, "--project-files");
        var repositoryFiles = TakeFlag(options, "--repository-files");
        var everything = TakeFlag(options, "--all");
        var title = TakeValue(options, "--title");
        var body = TakeValue(options, "--body");
        var branch = TakeValue(options, "--branch");
        var target = TakeValue(options, "--target");
        var dryRun = TakeFlag(options, "--dry-run");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 0, 0, "");

        var install = await LoadInstallAsync();
        BasisContributeScan scan = null!;
        await RunOperationAsync(async ct => scan = await _contribute.ScanAsync(install.RepoRoot, install.UnityProjectPath, Out.Progress, ct), "Comparing the project with its Basis version" + Out.Ellipsis);
        if (scan.IsBlocked) throw new InvalidOperationException(DescribeContributeBlock(scan));
        if (!everything && !projectFiles && !repositoryFiles && packages.Count == 0 && paths.Count == 0)
        {
            PrintContributeScan(scan);
            return;
        }

        var selected = new List<string>();
        foreach (var id in packages)
            selected.AddRange((scan.FindPackage(id) ?? throw new InvalidOperationException($"{id} has no changes compared with Basis.")).Changes.Select(c => c.Path));
        foreach (var group in scan.Groups)
            if (everything || (projectFiles && group.Area == BasisChangeArea.Project) || (repositoryFiles && group.Area == BasisChangeArea.Repository))
                selected.AddRange(group.Changes.Select(c => c.Path));
        foreach (var path in paths)
        {
            var wanted = path.Replace('\\', '/').Trim('/');
            var matches = scan.Changes.Where(c => IsAtOrUnder(c.Path, wanted) || IsAtOrUnder(c.ProjectPath, wanted)).Select(c => c.Path).ToList();
            if (matches.Count == 0) throw new InvalidOperationException($"{path} has no changes compared with Basis.");
            selected.AddRange(matches);
        }
        var changes = BasisContributeService.ExpandSelection(scan, selected);
        Out.Heading($"{Plural(changes.Count, "file")} selected");
        foreach (var change in changes.Take(40)) Out.Say(new Span($"  {KindLetter(change.Kind)} ", KindTone(change.Kind)), new Span(change.Path));
        if (changes.Count > 40) Out.Note($"  {Out.Ellipsis} and {changes.Count - 40} more");
        var picked = changes.Select(c => c.Path).ToList();

        if (dryRun)
        {
            var name = await _git.GetConfigValueAsync(scan.RepoRoot, "user.name") ?? "Basis Package Manager";
            var email = await _git.GetConfigValueAsync(scan.RepoRoot, "user.email") ?? "basispm@localhost";
            var message = BasisContributeService.ComposeMessage(new BasisPullRequestDraft(title ?? $"Changes from {install.DisplayName}", body, "", ""));
            BasisContributeCommit commit = null!;
            await RunOperationAsync(async ct => commit = await _contribute.CreateCommitAsync(scan, picked, message, name, email, ct), "Building the commit" + Out.Ellipsis);
            if (!commit.Ok) throw new InvalidOperationException(commit.Error);
            Out.Success($"Built commit {Short(commit.Commit)} on Basis {Short(scan.Base!.Sha)} with {Plural(commit.FileCount, "file")}. Nothing was pushed.");
            Out.Hint($"See it with 'git show --stat {commit.Commit}'.");
            return;
        }

        if (string.IsNullOrWhiteSpace(title)) throw new UsageException("Add --title \"...\" to open a pull request, or --dry-run to only build the commit.");
        var draft = new BasisPullRequestDraft(title, body,
            branch ?? BasisContributeService.SuggestBranch(packages.Count == 1 ? packages[0] : install.DisplayName, DateTimeOffset.Now),
            target ?? BasisContributeService.SuggestTarget(scan));
        var token = await TryGitHubTokenAsync()
            ?? throw new InvalidOperationException("Sign in to GitHub first: run 'gh auth login', or set GH_TOKEN to a token that can open pull requests.");
        var user = await _ghApi.GetUserAsync(token)
            ?? throw new InvalidOperationException("GitHub didn't accept that token. Sign in again with 'gh auth login'.");
        if (!assumeYes && !Confirm($"Open a pull request on {_contribute.Owner}/{_contribute.Repo} ({draft.TargetBranch}) from branch {draft.Branch} as {user.Login}?"))
        {
            Out.Warning("Cancelled.");
            return;
        }
        BasisContributeResult result = null!;
        await RunOperationAsync(async ct => result = await _contribute.SubmitAsync(scan, picked, draft, token, user, Out.Progress, ct), "Opening the pull request" + Out.Ellipsis);
        if (!result.Ok)
        {
            if (result.CompareUrl is not null) Out.Say($"Open the pull request by hand: {result.CompareUrl}");
            throw new InvalidOperationException(result.Error);
        }
        Out.Success(result.Updated ? $"Updated the pull request: {result.Url}" : $"Opened a pull request: {result.Url}");
    }

    private void PrintContributeScan(BasisContributeScan scan)
    {
        var basis = scan.Base!;
        var commit = scan.BaseCommit is { } c ? $", {c.Date:yyyy-MM-dd}: {c.Subject}" : "";
        Out.Say(new Span("Based on Basis ", Tone.Dim), new Span(Short(basis.Sha), Tone.Accent), new Span($" ({basis.Branch}{commit})", Tone.Dim));
        if (scan.ChangeCount == 0)
        {
            Out.Success("Nothing differs from that Basis version.");
            return;
        }
        foreach (var group in scan.Groups)
        {
            var label = group.Area switch
            {
                BasisChangeArea.Package => group.InBasis ? $"Package {group.Name}" : $"Package {group.Name} (not in Basis)",
                BasisChangeArea.Project => "Project files",
                _ => "Repository files",
            };
            Out.Say();
            Out.Say(new Span(label, Tone.Bold), new Span($": {Plural(group.Changes.Count, "file")}", Tone.Dim));
            foreach (var change in group.Changes.Take(10)) Out.Say(new Span($"  {KindLetter(change.Kind)} ", KindTone(change.Kind)), new Span(group.RelativePath(change)));
            if (group.Changes.Count > 10) Out.Note($"  {Out.Ellipsis} and {group.Changes.Count - 10} more");
        }
        if (scan.Skipped.Count > 0) Out.Note($"Skipped {Plural(scan.Skipped.Count, "nested repository", "nested repositories")}: {string.Join(", ", scan.Skipped.Take(5))}");
        Out.Say();
        Out.Hint($"Send changes with '{Prefix}contribute --package <id> --title \"...\"' (or --project-files, --path <path>, --all; add --dry-run to only build the commit).");
    }

    private static bool IsAtOrUnder(string path, string wanted) =>
        path == wanted || path.StartsWith(wanted + "/", StringComparison.Ordinal);

    private static char KindLetter(BasisChangeKind kind) => kind switch
    {
        BasisChangeKind.Added => 'A',
        BasisChangeKind.Deleted => 'D',
        _ => 'M',
    };

    private static Tone KindTone(BasisChangeKind kind) => kind switch
    {
        BasisChangeKind.Added => Tone.Good,
        BasisChangeKind.Deleted => Tone.Bad,
        _ => Tone.Warn,
    };

    private static string DescribeContributeBlock(BasisContributeScan scan) => scan.Block switch
    {
        BasisContributeBlock.GitMissing => "Git was not found. Install Git and make sure it is on your PATH.",
        BasisContributeBlock.GitTooOld => $"Git {scan.Detail} is too old. Install Git {BasisUpdateService.MinimumGitVersion.ToString(3)} or newer.",
        BasisContributeBlock.NotGitRepo => "This project isn't tracked by git, so it can't be compared with Basis. Run 'update-basis --init-git' first.",
        BasisContributeBlock.NoCommits => "This project's git repository has no commits yet.",
        BasisContributeBlock.UpdateInProgress => "A Basis update or branch switch is waiting for decisions. Finish it first (run 'conflicts').",
        BasisContributeBlock.OperationInProgress => $"A git {scan.Detail} is in progress. Finish or abort it first.",
        BasisContributeBlock.BaseUnknown => scan.Detail is { Length: > 0 } reason
            ? $"Couldn't tell which Basis version this project is based on: {reason}"
            : "Couldn't tell which Basis version this project is based on. Its files don't match any Basis version closely enough.",
        BasisContributeBlock.BaseMissing => $"Basis commit {Short(scan.Detail)} couldn't be downloaded from BasisVR/Basis.",
        BasisContributeBlock.BasisFolderMissing => $"The Basis version this project is based on has no '{scan.Detail}' folder.",
        _ => scan.Detail is { Length: > 0 } detail ? $"Couldn't read this project's files: {detail}" : "Couldn't read this project's files.",
    };
}
