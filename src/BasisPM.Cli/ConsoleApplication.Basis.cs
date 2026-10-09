using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private async Task ListBranchesAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        IReadOnlyDictionary<string, string> heads = null!;
        await RunOperationAsync(async ct => heads = await _updates.GetBasisHeadsAsync(refresh: true, ct), "Asking BasisVR/Basis for its branches" + Out.Ellipsis);
        var (followed, source) = await _updates.DescribeBasisBranchAsync(install.RepoRoot, heads);
        var basis = BasisUpdateService.OrderBranches(heads.Keys);
        var own = await _updates.ListProjectBranchesAsync(install.RepoRoot);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                follows = followed, followsBecause = source,
                basisBranches = basis.Select(b => new { name = b, sha = heads[b], followed = b == followed }),
                projectBranches = own.Select(b => new { name = b.Name, remote = b.Remote, current = b.IsCurrent }),
            });
            return;
        }
        Out.Heading($"Basis branches ({_updates.UpstreamUrl})");
        foreach (var name in basis)
            Out.Say(new Span(name == followed ? "* " : "  ", Tone.Good), new Span(name, name == followed ? Tone.Good : Tone.Plain), new Span(name == followed ? $"  followed: {DescribeSource(source)}" : "", Tone.Dim));
        Out.Say();
        Out.Heading("Your project's branches");
        foreach (var branch in own)
            Out.Say(new Span(branch.IsCurrent ? "* " : "  ", Tone.Good), new Span(branch.Display, branch.IsCurrent ? Tone.Good : branch.Remote is null ? Tone.Plain : Tone.Dim));
        Out.Say();
        Out.Hint($"'{Prefix}change-branch <name>' switches to one of your branches; '{Prefix}update-basis --branch <name>' moves this branch onto another Basis branch.");
    }

    private async Task ChangeBranchAsync(List<string> values)
    {
        var options = values.ToList();
        var install = await LoadInstallAsync();
        if (TakeFlag(options, "--continue"))
        {
            Expect(options, 0, 0, "");
            var state = await _updates.LoadStateAsync(install.RepoRoot);
            BasisUpdateResult continued = null!;
            await RunOperationAsync(async ct => continued = await _updates.ContinueAsync(install.RepoRoot, Out.Progress, ct), "Finishing the switch" + Out.Ellipsis);
            await ReportUpdateAsync(install, continued, state);
            return;
        }
        if (TakeFlag(options, "--abort"))
        {
            Expect(options, 0, 0, "");
            await AbortAsync(install);
            return;
        }
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 1, 1, "the branch to switch to");

        await RunOperationAsync(ct => _git.FetchAsync(install.RepoRoot, Out.Progress, ct), "Fetching your remotes" + Out.Ellipsis);
        var branches = await _updates.ListProjectBranchesAsync(install.RepoRoot);
        var name = options[0];
        var target = branches.FirstOrDefault(b => b.Display == name) ?? branches.FirstOrDefault(b => b.Name == name);
        if (target is null)
        {
            var suggestions = Suggest(name, branches.Select(b => b.Display));
            throw new InvalidOperationException($"'{name}' isn't one of this project's branches." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" Run '{Prefix}branches' to list them.")
                + $" To move onto a Basis branch, run '{Prefix}update-basis --branch {name}'.");
        }
        if (target.IsCurrent)
        {
            Out.Success($"Already on {target.Name}.");
            return;
        }
        var plan = await _updates.PlanBranchSwitchAsync(install.RepoRoot, target);
        if (plan.InProgress) throw new InvalidOperationException($"A Basis update or branch switch is waiting for decisions. Run '{Prefix}conflicts' to see it.");
        if (plan.BlockingPaths.Count > 0)
            throw new InvalidOperationException($"Files ignored by git are in the way of files on {target.Name}. Remove or move them, then try again:\n  " + string.Join("\n  ", plan.BlockingPaths.Take(10)));
        if (!plan.CanSwitch) throw new InvalidOperationException(DescribeSwitchBlock(plan));
        if (UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
        {
            if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Unity has this project open. Close Unity, then switch branches again.");
            Out.Warning("Unity may still have this project open. Close it first if it is running.");
        }
        if (plan.CollidingPaths.Count > 0)
        {
            Out.Say($"{Plural(plan.CollidingPaths.Count, "file")} you're editing {(plan.CollidingPaths.Count == 1 ? "is" : "are")} different on {target.Name}. They're set aside, then merged back after the switch:");
            foreach (var path in plan.CollidingPaths.Take(10)) Out.Say("  " + path);
            if (plan.CollidingPaths.Count > 10) Out.Note($"  {Out.Ellipsis} and {plan.CollidingPaths.Count - 10} more");
            if (!assumeYes && !Confirm("Switch now?"))
            {
                Out.Warning("Switch cancelled.");
                return;
            }
        }

        BasisUpdateResult result = null!;
        await RunOperationAsync(async ct => result = await _updates.SwitchBranchAsync(install.RepoRoot, plan, Out.Progress, ct), $"Switching to {target.Name}" + Out.Ellipsis);
        await ReportUpdateAsync(install, result, new BasisUpdateState { Operation = BasisOperation.BranchSwitch, TargetBranch = target.Name, LocalBranch = plan.CurrentBranch ?? "" });
    }

    private async Task AbortAsync(BasisInstall install)
    {
        var state = await _updates.LoadStateAsync(install.RepoRoot);
        await ReportUpdateAsync(install, await _updates.AbortAsync(install.RepoRoot), state);
    }

    private async Task UpdateBasisAsync(List<string> values)
    {
        var options = values.ToList();
        var install = await LoadInstallAsync();
        if (TakeFlag(options, "--continue"))
        {
            Expect(options, 0, 0, "");
            var state = await _updates.LoadStateAsync(install.RepoRoot);
            BasisUpdateResult continued = null!;
            await RunOperationAsync(async ct => continued = await _updates.ContinueAsync(install.RepoRoot, Out.Progress, ct), "Finishing the update" + Out.Ellipsis);
            await ReportUpdateAsync(install, continued, state);
            return;
        }
        if (TakeFlag(options, "--abort"))
        {
            Expect(options, 0, 0, "");
            await AbortAsync(install);
            return;
        }
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var link = TakeFlag(options, "--link");
        var initGit = TakeFlag(options, "--init-git");
        var backup = TakeFlag(options, "--backup");
        var branch = TakeValue(options, "--branch");
        Expect(options, 0, 0, "");

        var plan = await PlanUpdateAsync(install, branch);
        if (plan.Kind == BasisUpdateKind.NotGitRepo)
        {
            if (!initGit)
                throw new InvalidOperationException($"This project isn't tracked by git. Run '{Prefix}update-basis --init-git' to record it in a new local git repository first (nothing is uploaded).");
            BasisStepResult init = null!;
            try { await RunOperationAsync(async ct => init = await _updates.InitializeRepositoryAsync(install.RepoRoot, install.UnityProjectPath, Out.Progress, ct), "Recording the project in git" + Out.Ellipsis); }
            catch (OperationCanceledException)
            {
                Out.Hint($"Git setup stopped partway. Running '{Prefix}update-basis --init-git' again picks up where it left off.");
                throw;
            }
            if (!init.Ok) throw new InvalidOperationException(DescribeFailure(init.Failure, init.Detail));
            Out.Success("Recorded the project in a new local git repository.");
            PrintGitSetup(init);
            plan = await PlanUpdateAsync(install, branch);
        }
        if (link && plan is { Kind: BasisUpdateKind.Apply, BaseSource: BasisBaseSource.Similarity, SuggestedBase: { } match })
        {
            var linked = await _updates.LinkAsync(install.RepoRoot, match.Sha);
            if (!linked.Ok) throw new InvalidOperationException(DescribeFailure(linked.Failure, linked.Detail));
            Out.Success($"Linked this project's history to Basis {match.ShortSha}. Pushing it now uploads Basis's history too.");
            plan = await PlanUpdateAsync(install, branch);
        }

        switch (plan.Kind)
        {
            case BasisUpdateKind.InProgress:
                Out.Warning("A Basis update or branch switch is already waiting for decisions.");
                await ShowConflictsAsync(new List<string>());
                return;
            case BasisUpdateKind.UpToDate when plan.IsSwitch:
                var follow = await _updates.SetBasisBranchAsync(install.RepoRoot, plan.BasisBranch);
                if (!follow.Ok) throw new InvalidOperationException("Couldn't save the Basis branch. Make sure the project is on a git branch.");
                Out.Success($"This project already matches Basis {plan.BasisBranch} ({Short(plan.UpstreamSha)}), and now follows it.");
                return;
            case BasisUpdateKind.UpToDate:
                Out.Success($"Already up to date with Basis {plan.BasisBranch} ({Short(plan.UpstreamSha)}).");
                return;
            case BasisUpdateKind.Blocked:
                throw new InvalidOperationException(DescribeBlock(plan));
            case BasisUpdateKind.Unrelated:
                throw new InvalidOperationException("This project's files don't match any Basis version closely enough to find where it started. It may use a different folder layout than BasisVR/Basis, or not be based on Basis.");
            case BasisUpdateKind.FastForward or BasisUpdateKind.Merge or BasisUpdateKind.Apply:
                break;
            default:
                throw new InvalidOperationException("This project can't be updated automatically.");
        }

        PrintPlan(plan);
        if (plan.BlockingPaths.Count > 0)
            throw new InvalidOperationException("Files ignored by git are in the way of new Basis files. Remove or move them, then try again:\n  " + string.Join("\n  ", plan.BlockingPaths.Take(10)));
        if (UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
        {
            if (OperatingSystem.IsWindows()) throw new InvalidOperationException("Unity has this project open. Close Unity, then run the update again.");
            Out.Warning("Unity may still have this project open. Close it first if it is running.");
        }
        if (!assumeYes && !Confirm(plan.IsSwitch ? $"Move to Basis {plan.BasisBranch} now?" : "Update now?"))
        {
            Out.Warning("Update cancelled.");
            return;
        }
        if (backup)
        {
            var zip = await CreateBackupAsync(install, null);
            Out.Success($"Backed up the project to {zip}.");
        }

        BasisUpdateResult result = null!;
        await RunOperationAsync(async ct => result = await _updates.ApplyAsync(install.RepoRoot, plan, Out.Progress, ct), $"Updating to Basis {plan.BasisBranch}" + Out.Ellipsis);
        await ReportUpdateAsync(install, result, null, plan);
    }

    private static void PrintGitSetup(BasisStepResult init)
    {
        if (init.AddedFiles.Count > 0) Out.Say($"Added Basis's {string.Join(", ", init.AddedFiles)}, so Unity's caches and generated files stay out.");
        if (init.Unhidden.Count is var hidden and > 0)
            Out.Say($"A .gitignore was hiding {Plural(hidden, "package")} from git; {(hidden == 1 ? "it's" : "they're")} recorded now, so Basis updates can reach {(hidden == 1 ? "it" : "them")}.");
        if (init.LeftOut.Count > 0) Out.Say($"Left out {string.Join(", ", init.LeftOut)}: each is a git clone of its own.");
        if (init.LargeFiles.Count is var large and > 0)
        {
            Out.Warning($"{Plural(large, "file")} {(large == 1 ? "is" : "are")} over 100 MB, which GitHub won't accept without Git LFS:");
            foreach (var file in init.LargeFiles.Take(10)) Out.Say("  " + file);
            if (large > 10) Out.Note($"  {Out.Ellipsis} and {large - 10} more");
        }
    }

    private async Task<BasisUpdatePlan> PlanUpdateAsync(BasisInstall install, string? branch)
    {
        BasisUpdatePlan plan = null!;
        await RunOperationAsync(async ct => plan = await _updates.PlanAsync(install.RepoRoot, branch, Out.Progress, ct), "Comparing the project with Basis" + Out.Ellipsis);
        return plan;
    }

    private static void PrintPlan(BasisUpdatePlan plan)
    {
        if (plan.IsSwitch) Out.Say($"Moving {plan.LocalBranch} from Basis {plan.FromBranch} to Basis {plan.BasisBranch}.");
        if (plan is { BaseSource: BasisBaseSource.Similarity, SuggestedBase: { } match })
            Out.Say($"This project started from Basis {match.ShortSha} {match.Date:yyyy-MM-dd} \"{match.Subject}\" ({match.MatchingFiles:N0} of {match.BasisFiles:N0} files match).");
        if (!plan.Layout.IsWhole)
            Out.Say($"Basis's {plan.Layout.BasisFolder} folder is this project's {(plan.Layout.ProjectFolder.Length == 0 ? "root folder" : plan.Layout.ProjectFolder + " folder")}.");
        Out.Say(new Span($"Basis {plan.BasisBranch} {Out.Arrow} {plan.LocalBranch}: ", Tone.Bold), new Span($"{Plural(plan.IncomingCount, "new Basis commit")}."));
        if (plan.IsSwitch && plan.OutgoingCount > 0)
            Out.Say($"{Plural(plan.OutgoingCount, "Basis commit")} on {plan.FromBranch} {(plan.OutgoingCount == 1 ? "isn't" : "aren't")} on {plan.BasisBranch} and leave your project.");
        foreach (var commit in plan.IncomingCommits.Take(15))
            Out.Say(new Span($"  {commit.ShortSha} ", Tone.Accent), new Span($"{commit.Date:yyyy-MM-dd} ", Tone.Dim), new Span(commit.Subject));
        if (plan.IncomingCount > 15) Out.Note($"  {Out.Ellipsis} and {plan.IncomingCount - 15} more");
        Out.Say(plan.Kind switch
        {
            BasisUpdateKind.FastForward => "Your branch has no commits of its own, so it moves straight to the new Basis.",
            BasisUpdateKind.Apply when !plan.Connected => $"Your project keeps its own git history: the Basis changes become one new commit on {plan.LocalBranch} that records the Basis version, so nothing from Basis's history is added.",
            BasisUpdateKind.Apply => $"Everything you changed since Basis {Short(plan.MergeBase)} is kept and applied on top of Basis {plan.BasisBranch} in one new commit.",
            _ => $"Your {Plural(plan.LocalCommitCount, "commit")} will be merged with Basis in a new merge commit.",
        });
        if (plan.UncommittedCount > 0)
            Out.Say($"{Plural(plan.UncommittedCount, "uncommitted change")} stay as they are; {plan.CollidingPaths.Count} of them {(plan.CollidingPaths.Count == 1 ? "is a file" : "are files")} Basis also changes and will be merged back after the update.");
        if (plan.PredictedConflicts is { Count: > 0 } predicted)
        {
            Out.Warning($"{Plural(predicted.Count, "file")} changed on both sides will need a decision:");
            foreach (var path in predicted.Take(10)) Out.Say("  " + path);
            if (predicted.Count > 10) Out.Note($"  {Out.Ellipsis} and {predicted.Count - 10} more");
        }
    }

    private async Task ReportUpdateAsync(BasisInstall before, BasisUpdateResult result, BasisUpdateState? state = null, BasisUpdatePlan? plan = null)
    {
        var switching = state?.Operation == BasisOperation.BranchSwitch;
        switch (result.Kind)
        {
            case BasisUpdateResultKind.Updated:
                var after = await _installs.LoadAsync(before.RepoRoot, before.Alias);
                Out.Success(switching ? $"Switched {after.DisplayName} to {state!.TargetBranch}."
                    : plan is { IsSwitch: true } ? $"Moved {after.DisplayName} from Basis {plan.FromBranch} to Basis {plan.BasisBranch} ({Short(result.NewHead)})."
                    : $"Updated {after.DisplayName} to the latest Basis ({Short(result.NewHead)}).");
                if (!string.Equals(before.UnityVersion, after.UnityVersion, StringComparison.Ordinal))
                    Out.Warning($"This project now uses Unity {after.UnityVersion} (was {before.UnityVersion}). '{Prefix}unity install' gets it.");
                break;
            case BasisUpdateResultKind.Aborted:
                Out.Success(switching
                    ? $"Switch undone. The project is back on {state!.LocalBranch} with your edits."
                    : "Update cancelled. The project is back to how it was before the update.");
                break;
            case BasisUpdateResultKind.Conflicts:
                PrintConflicts(result.Phase ?? BasisUpdatePhase.Merging, result.Conflicts ?? Array.Empty<BasisConflict>(), switching ? state!.TargetBranch : null);
                break;
            default:
                throw new InvalidOperationException(DescribeFailure(result.Failure, result.Detail));
        }
    }

    private async Task CheckUpdatesAsync(List<string> values)
    {
        var options = values.ToList();
        var all = TakeFlag(options, "--all");
        Expect(options, 0, 0, "");
        if (all)
        {
            await CheckAllProjectsAsync();
            return;
        }
        var install = await LoadInstallAsync();
        BasisUpdateCheck check = null!;
        await RunOperationAsync(async ct => check = await _updates.CheckAsync(install.RepoRoot, allowFetch: true, Out.Progress, ct), "Checking BasisVR/Basis" + Out.Ellipsis);
        if (Out.JsonMode)
        {
            Out.Json(CheckJson(install.DisplayName, install.RepoRoot, check));
            if (check.Status == BasisUpdateStatus.Error) _exitCode = 1;
            return;
        }
        switch (check.Status)
        {
            case BasisUpdateStatus.UpToDate:
                Out.Success($"Up to date with Basis {check.BasisBranch}.");
                break;
            case BasisUpdateStatus.UpdateAvailable when check.InProgress:
                Out.Warning(check.Operation == BasisOperation.BranchSwitch
                    ? $"A switch to {check.BasisBranch} is waiting for decisions. Run '{Prefix}conflicts' to see what still needs one."
                    : $"A Basis update is in progress. Run '{Prefix}conflicts' to see what still needs a decision.");
                break;
            case BasisUpdateStatus.UpdateAvailable:
                Out.Say(new Span("! ", Tone.Warn), new Span(check.Behind is int behind ? $"Basis {check.BasisBranch} has {Plural(behind, "new commit")}." : $"A newer Basis {check.BasisBranch} is available."));
                Out.Hint($"Run '{Prefix}update-basis' to see them and update.");
                break;
            case BasisUpdateStatus.NotGitRepo:
                Out.Warning($"This project isn't tracked by git, so updates can't be checked. Run '{Prefix}update-basis --init-git' to set that up.");
                break;
            default:
                throw new InvalidOperationException(check.Detail ?? "Couldn't check for Basis updates.");
        }
    }

    private async Task CheckAllProjectsAsync()
    {
        var settings = await _settings.LoadAsync();
        var projects = SavedProjects(settings).Where(p => Directory.Exists(p.Root)).ToList();
        if (projects.Count == 0) throw new InvalidOperationException($"You have no saved projects. Add one with '{Prefix}projects add <path>'.");
        var results = new List<(string Name, string Root, BasisUpdateCheck Check)>();
        await RunOperationAsync(async ct =>
        {
            await _updates.GetBasisHeadsAsync(refresh: true, ct);
            foreach (var (root, name) in projects)
            {
                Out.Progress($"Checking {name}" + Out.Ellipsis);
                try { results.Add((name, root, await _updates.CheckAsync(root, allowFetch: true, Out.Progress, ct))); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiagnosticLog.Write($"Checking {root} for a Basis update", ex);
                    results.Add((name, root, new BasisUpdateCheck(BasisUpdateStatus.Error, "", null, null, false, ex.Message)));
                }
            }
        }, "Checking your projects against BasisVR/Basis" + Out.Ellipsis);
        if (results.Any(r => r.Check.Status == BasisUpdateStatus.Error)) _exitCode = 1;
        if (Out.JsonMode)
        {
            Out.Json(results.Select(r => CheckJson(r.Name, r.Root, r.Check)));
            return;
        }
        Out.Table(results.Select(r => new[] { new Span(r.Name, Tone.Bold), CheckSpan(r.Check) }).ToList());
        var available = results.Count(r => r.Check.Status == BasisUpdateStatus.UpdateAvailable && !r.Check.InProgress);
        if (available > 0) Out.Hint($"'{Prefix}update-all' updates every project that doesn't need a decision.");
    }

    private static object CheckJson(string name, string root, BasisUpdateCheck check) => new
    {
        project = name, path = root, status = check.Status, basisBranch = check.BasisBranch, latest = check.RemoteSha, behind = check.Behind,
        waiting = check.InProgress ? check.Operation : (BasisOperation?)null, detail = check.Detail,
    };

    private Span CheckSpan(BasisUpdateCheck check) => check.Status switch
    {
        BasisUpdateStatus.UpToDate => new($"{Out.Check} up to date with {check.BasisBranch}", Tone.Good),
        BasisUpdateStatus.UpdateAvailable when check.InProgress => new($"! waiting for decisions ({Prefix}conflicts)", Tone.Warn),
        BasisUpdateStatus.UpdateAvailable => new(check.Behind is int behind ? $"! {Plural(behind, "new commit")} on {check.BasisBranch}" : $"! newer {check.BasisBranch} available", Tone.Warn),
        BasisUpdateStatus.NotGitRepo => new("not in git", Tone.Dim),
        _ => new($"{Out.Cross} {check.Detail ?? "couldn't check"}", Tone.Bad),
    };

    private async Task UpdateAllAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 0, 0, "");
        if (!_git.IsAvailable) throw new InvalidOperationException("Git was not found on PATH.");
        var settings = await _settings.LoadAsync();
        var projects = SavedProjects(settings).Where(p => Directory.Exists(p.Root)).ToList();
        if (projects.Count == 0) throw new InvalidOperationException($"You have no saved projects. Add one with '{Prefix}projects add <path>'.");

        var ready = new List<(string Root, string Name, BasisUpdatePlan Plan)>();
        var waiting = new List<(string Name, string Reason)>();
        var current = 0;
        await RunOperationAsync(async ct =>
        {
            foreach (var (root, name) in projects)
            {
                Out.Progress($"Checking {name} ({++current} of {projects.Count})" + Out.Ellipsis);
                try
                {
                    var plan = await _updates.PlanAsync(root, null, Out.Progress, ct);
                    if (plan.Kind == BasisUpdateKind.UpToDate) continue;
                    if (WhyNotAutomatic(root, plan) is { } reason) waiting.Add((name, reason));
                    else ready.Add((root, name, plan));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiagnosticLog.Write($"Checking {root} against the latest Basis", ex);
                    waiting.Add((name, ex.Message));
                }
            }
        }, "Checking your projects against Basis" + Out.Ellipsis);

        if (ready.Count == 0)
        {
            if (waiting.Count == 0) Out.Success("Every project is up to date with the Basis branch it follows.");
            else PrintWaiting(waiting);
            return;
        }
        Out.Heading("Ready to update");
        foreach (var (_, name, plan) in ready) Out.Say($"  {name}: {Plural(plan.IncomingCount, "new Basis commit")} on {plan.BasisBranch}");
        if (waiting.Count > 0) PrintWaiting(waiting);
        if (!assumeYes && !Confirm($"Update {Plural(ready.Count, "project")} now?"))
        {
            Out.Warning("Cancelled.");
            return;
        }

        var updated = new List<string>();
        var failed = 0;
        for (var i = 0; i < ready.Count; i++)
        {
            var (root, name, plan) = ready[i];
            var before = await _installs.LoadAsync(root, AliasOf(settings, root));
            if (OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(before.UnityProjectPath))
            {
                waiting.Add((name, "Unity has it open"));
                continue;
            }
            BasisUpdateResult result;
            try
            {
                BasisUpdateResult applied = null!;
                await RunOperationAsync(async ct => applied = await _updates.ApplyAsync(root, plan, Out.Progress, ct), $"Updating {name} ({i + 1} of {ready.Count})" + Out.Ellipsis);
                result = applied;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DiagnosticLog.Write($"Updating {root} to the latest Basis", ex);
                waiting.Add((name, ex.Message));
                failed++;
                continue;
            }
            switch (result.Kind)
            {
                case BasisUpdateResultKind.Updated:
                    updated.Add(name);
                    Out.Success($"Updated {name} to Basis {plan.BasisBranch} ({Short(result.NewHead)}).");
                    var after = await _installs.LoadAsync(root);
                    if (after.HasUnityProject && !string.Equals(before.UnityVersion, after.UnityVersion, StringComparison.Ordinal))
                        Out.Warning($"{name} now uses Unity {after.UnityVersion} (was {before.UnityVersion}).");
                    break;
                case BasisUpdateResultKind.Conflicts:
                    waiting.Add((name, $"stopped on files that need a decision: run '{Prefix}conflicts --project \"{name}\"'"));
                    break;
                default:
                    waiting.Add((name, DescribeFailure(result.Failure, result.Detail)));
                    failed++;
                    break;
            }
        }
        if (updated.Count > 0 && waiting.Count == 0) Out.Success($"All done: {string.Join(", ", updated)}.");
        if (waiting.Count > 0) PrintWaiting(waiting);
        if (failed > 0) _exitCode = 1;
    }

    private void PrintWaiting(IReadOnlyList<(string Name, string Reason)> waiting)
    {
        Out.Heading($"Needs you (update these on their own with '{Prefix}update-basis --project <name>')");
        foreach (var (name, reason) in waiting) Out.Say(new Span($"  {name}: ", Tone.Warn), new Span(reason));
    }

    private string? WhyNotAutomatic(string root, BasisUpdatePlan plan) => plan.Kind switch
    {
        BasisUpdateKind.InProgress => "an update or branch switch is waiting for decisions",
        BasisUpdateKind.NotGitRepo => $"not in git yet ('{Prefix}update-basis --init-git' sets it up)",
        BasisUpdateKind.Blocked => DescribeBlock(plan),
        _ when !plan.CanApply => "needs a review",
        _ when plan.IsSwitch => $"would move from Basis {plan.FromBranch} to {plan.BasisBranch}",
        _ when plan.BaseSource == BasisBaseSource.Similarity => "first update of a copy: check where it started",
        _ when plan.PredictedConflicts is { Count: > 0 } conflicts => $"{Plural(conflicts.Count, "file")} changed on both sides",
        _ when OperatingSystem.IsWindows() && UnityProjectService.IsOpenInUnity(_projects.Detect(root).ResolvedPath ?? root) => "Unity has it open",
        _ => null,
    };

    private async Task ShowConflictsAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        var state = await _updates.LoadStateAsync(install.RepoRoot);
        var conflicts = state is null ? Array.Empty<BasisConflict>() : await _updates.GetConflictsAsync(install.RepoRoot);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                waiting = state is not null, operation = state?.Operation, phase = state?.Phase, branch = state?.Operation == BasisOperation.BranchSwitch ? state.TargetBranch : state?.BasisBranch,
                conflicts = conflicts.Select(c => new { path = c.Path, kind = c.Kind, unityAsset = c.IsUnityAsset }),
            });
            return;
        }
        if (state is null)
        {
            Out.Success("No Basis update or branch switch is in progress.");
            return;
        }
        PrintConflicts(state.Phase, conflicts, state.Operation == BasisOperation.BranchSwitch ? state.TargetBranch : null);
    }

    private void PrintConflicts(BasisUpdatePhase phase, IReadOnlyList<BasisConflict> conflicts, string? branch = null)
    {
        Out.Warning(branch is not null ? $"You're now on {branch}, but some of your uncommitted edits overlap changes on {branch}. Choose a version for each one:"
            : phase == BasisUpdatePhase.Merging ? "Basis and your project changed the same files. Choose a version for each one:"
            : "Basis is updated, but some of your uncommitted edits overlap the new Basis changes. Choose a version for each one:");
        foreach (var conflict in conflicts)
            Out.Say(new Span($"  [{DescribeConflict(conflict.Kind, branch)}] ", Tone.Warn), new Span(conflict.Path), new Span(conflict.IsUnityAsset ? "  (Unity asset: pick a version, don't hand-merge)" : "", Tone.Dim));
        var command = branch is null ? "update-basis" : "change-branch";
        Out.Hint(conflicts.Count == 0
            ? $"Nothing left to decide. Run '{Prefix}{command} --continue' to finish."
            : branch is null
                ? $"Use '{Prefix}resolve <path> mine|basis|done' (or 'resolve --all mine|basis'), then '{Prefix}update-basis --continue'. '{Prefix}update-basis --abort' puts everything back."
                : $"Use '{Prefix}resolve <path> mine|theirs|done' (or 'resolve --all mine|theirs'), then '{Prefix}change-branch --continue'. '{Prefix}change-branch --abort' goes back to where you were.");
    }

    private async Task ResolveConflictAsync(List<string> values)
    {
        var options = values.ToList();
        var all = TakeFlag(options, "--all");
        Expect(options, all ? 1 : 2, all ? 1 : 2, all ? "mine, basis or theirs" : "a path and mine, basis, theirs or done");
        var choice = options[^1].ToLowerInvariant() switch
        {
            "mine" or "ours" => ConflictChoice.Mine,
            "basis" or "theirs" => ConflictChoice.Basis,
            "done" => ConflictChoice.Resolved,
            var other => throw new UsageException($"'{other}' isn't a choice. Use mine, basis (theirs during a branch switch) or done."),
        };
        if (all && choice == ConflictChoice.Resolved) throw new UsageException("'--all' needs mine, basis or theirs.");
        var install = await LoadInstallAsync();
        var conflicts = await _updates.GetConflictsAsync(install.RepoRoot);
        var targets = all ? conflicts.Select(c => c.Path).ToList() : new List<string> { options[0].Replace('\\', '/') };
        if (!all && conflicts.Count > 0 && !conflicts.Any(c => c.Path.Equals(targets[0], StringComparison.Ordinal)))
        {
            var suggestions = Suggest(targets[0], conflicts.Select(c => c.Path));
            if (suggestions.Count > 0) throw new InvalidOperationException($"{targets[0]} doesn't need a decision. Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?");
        }
        foreach (var path in targets)
        {
            var resolved = await _updates.ResolveAsync(install.RepoRoot, path, choice);
            if (!resolved.Ok) throw new InvalidOperationException(DescribeFailure(resolved.Failure, resolved.Detail));
            Out.Success($"Resolved {path}.");
        }
        var remaining = (await _updates.GetConflictsAsync(install.RepoRoot)).Count;
        var command = (await _updates.LoadStateAsync(install.RepoRoot))?.Operation == BasisOperation.BranchSwitch ? "change-branch" : "update-basis";
        Out.Hint(remaining == 0
            ? $"All files are decided. Run '{Prefix}{command} --continue' to finish."
            : $"{Plural(remaining, "file")} still {(remaining == 1 ? "needs" : "need")} a decision.");
    }

    private async Task BasisBranchAsync(List<string> values)
    {
        Expect(values, 0, 1, "a Basis branch");
        var install = await LoadInstallAsync();
        IReadOnlyDictionary<string, string> heads = null!;
        await RunOperationAsync(async ct => heads = await _updates.GetBasisHeadsAsync(refresh: true, ct), "Asking BasisVR/Basis for its branches" + Out.Ellipsis);
        if (values.Count == 1)
        {
            if (heads.Count > 0 && !heads.ContainsKey(values[0]))
            {
                var suggestions = Suggest(values[0], heads.Keys);
                throw new InvalidOperationException($"Basis has no branch called '{values[0]}'." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" Run '{Prefix}basis-branch' to list them."));
            }
            var set = await _updates.SetBasisBranchAsync(install.RepoRoot, values[0]);
            if (!set.Ok) throw new InvalidOperationException("Couldn't save the Basis branch. Make sure the project is on a git branch.");
            Out.Success($"This branch now follows Basis {values[0]}. Run '{Prefix}update-basis' to move onto it.");
            return;
        }
        var (followed, source) = await _updates.DescribeBasisBranchAsync(install.RepoRoot, heads);
        var basis = await _updates.ResolveBasisBaseAsync(install.RepoRoot);
        var ordered = BasisUpdateService.OrderBranches(heads.Keys);
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                follows = followed, followsBecause = source,
                builtOn = basis is null ? null : new { sha = basis.Sha, branch = basis.Branch, source = basis.Source, connected = basis.Connected, layout = basis.Layout.IsWhole ? null : basis.Layout.ToString() },
                branches = ordered,
            });
            return;
        }
        Out.Say(new Span("Follows Basis  ", Tone.Dim), new Span(followed, Tone.Accent), new Span($"  ({DescribeSource(source)})", Tone.Dim));
        if (basis is not null) Out.Say(new Span("Built on       ", Tone.Dim), new Span(Short(basis.Sha), Tone.Accent), new Span($"  ({DescribeBase(basis)})", Tone.Dim));
        Out.Say();
        Out.Heading("Basis branches");
        foreach (var name in ordered.Take(12)) Out.Say(new Span(name == followed ? "* " : "  ", Tone.Good), new Span(name));
        if (ordered.Count > 12) Out.Note($"  {Out.Ellipsis} and {ordered.Count - 12} more ('{Prefix}branches' lists them all)");
        Out.Hint($"'{Prefix}basis-branch <name>' makes the next update-basis move onto another one.");
    }

    private static string DescribeSource(BasisBranchSource source) => source switch
    {
        BasisBranchSource.Saved => "chosen with basis-branch",
        BasisBranchSource.Recorded => "recorded by the last Basis update",
        BasisBranchSource.Tracking => "the Basis branch your git branch tracks",
        BasisBranchSource.History => "found from your git history",
        BasisBranchSource.Estimated => "estimated by comparing your files with Basis",
        BasisBranchSource.SameName => "same name as your git branch",
        _ => "default",
    };

    private static string DescribeBase(BasisBaseInfo basis) => basis.Source switch
    {
        BasisBaseSource.Recorded => basis.Layout.IsWhole ? "recorded by the last Basis update" : $"recorded by the last Basis update, Basis folder mapping {basis.Layout}",
        BasisBaseSource.Similarity => "estimated by comparing your files with Basis",
        _ => "from your git history",
    };

    private static string DescribeBlock(BasisUpdatePlan plan) => plan.Block switch
    {
        BasisUpdateBlock.GitMissing => "Git was not found on PATH.",
        BasisUpdateBlock.GitTooOld => $"Git {plan.Detail} is too old; Basis updates need Git {BasisUpdateService.MinimumGitVersion.ToString(2)} or newer.",
        BasisUpdateBlock.DetachedHead => "The project isn't on a branch. Switch to a branch first (change-branch <name>).",
        BasisUpdateBlock.OperationInProgress => $"A git {plan.Detail} is already in progress in this project. Finish or abort it first.",
        BasisUpdateBlock.BranchNotFound => $"Basis has no branch called '{plan.Detail}'. Pick another with --branch.",
        BasisUpdateBlock.ShallowClone => "This is a shallow clone, so its history can't be matched to Basis. Run 'git fetch --unshallow' first.",
        BasisUpdateBlock.BasisFolderMissing => $"Basis {plan.BasisBranch} has no '{plan.Detail}' folder, so it can't be applied to this project's layout.",
        _ => $"Couldn't download Basis: {plan.Detail}",
    };

    private static string DescribeSwitchBlock(BranchSwitchPlan plan) => plan.Block switch
    {
        BasisUpdateBlock.GitMissing => "Git was not found on PATH.",
        BasisUpdateBlock.OperationInProgress => $"A git {plan.Detail} is already in progress in this project. Finish or abort it first.",
        BasisUpdateBlock.DetachedHead => "The project isn't on a branch, so your edited files can't be brought along. Commit or stash them first.",
        _ => $"Couldn't find the branch '{plan.Target.Display}'.",
    };

    private static string DescribeFailure(BasisUpdateFailure failure, string? detail) => failure switch
    {
        BasisUpdateFailure.HeadMoved => "The project changed since it was checked. Run the command again.",
        BasisUpdateFailure.AlreadyInProgress => "A Basis update or branch switch is already in progress. Run 'conflicts' to see it.",
        BasisUpdateFailure.NoUpdateInProgress => "No Basis update or branch switch is in progress.",
        BasisUpdateFailure.IgnoredFilesInTheWay => "Files ignored by git are in the way:\n  " + detail?.Replace("\n", "\n  "),
        BasisUpdateFailure.MarkersRemain => $"{detail} still contains conflict markers (<<<<<<< / >>>>>>>). Finish editing it first.",
        BasisUpdateFailure.LibraryNotIgnored => $"'{detail}' isn't covered by a .gitignore, so git would record the Library cache. Add a Unity .gitignore first.",
        BasisUpdateFailure.SetAsideFailed => $"Couldn't set your edits aside first: {detail}",
        BasisUpdateFailure.MergeFailed => $"Git couldn't merge Basis, so nothing was changed: {detail}",
        BasisUpdateFailure.CommitFailed => $"Couldn't record the update: {detail}",
        BasisUpdateFailure.RestoreFailed => $"The update is done, but your edits couldn't be put back yet (they are safe in the git stash): {detail}",
        BasisUpdateFailure.AbortFailed => $"Couldn't undo it: {detail}",
        BasisUpdateFailure.ResolveFailed => $"Couldn't resolve that file: {detail}",
        BasisUpdateFailure.LinkFailed => $"Couldn't link the project to Basis: {detail}",
        BasisUpdateFailure.InitFailed => $"Couldn't record the project in git: {detail}",
        BasisUpdateFailure.SwitchFailed => $"Git couldn't switch branches, so nothing was changed: {detail}",
        _ => "This project can't be updated automatically.",
    };

    private static string DescribeConflict(ConflictKind kind, string? branch = null) => (kind, branch) switch
    {
        (ConflictKind.BothChanged, null) => "both changed",
        (ConflictKind.BothAdded, null) => "both added",
        (ConflictKind.DeletedByYou, null) => "you deleted, Basis changed",
        (ConflictKind.DeletedByBasis, null) => "Basis deleted, you changed",
        (ConflictKind.BothChanged, _) => $"you and {branch} changed it",
        (ConflictKind.BothAdded, _) => $"you and {branch} added it",
        (ConflictKind.DeletedByYou, _) => $"you deleted, {branch} changed",
        (ConflictKind.DeletedByBasis, _) => $"{branch} deleted, you changed",
        _ => "both deleted",
    };
}
