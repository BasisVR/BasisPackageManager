using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisUpdateServiceTests
{
    private const string ThreeLines = "one\ntwo\nthree\n";

    private static (GitSandbox Box, string Project) StartWithClone()
    {
        var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", ThreeLines), ("b.txt", "b\n"), ("c.txt", "c\n"));
        return (box, box.CloneProject());
    }

    [GitFact]
    public async Task Check_reports_up_to_date_then_counts_new_basis_commits()
    {
        var (box, project) = StartWithClone();
        using var _ = box;

        var first = await box.Updates.CheckAsync(project);
        Assert.Equal(BasisUpdateStatus.UpToDate, first.Status);
        Assert.Equal("developer", first.BasisBranch);

        box.CommitUpstream("first", ("a.txt", "ONE\ntwo\nthree\n"));
        var tip = box.CommitUpstream("second", ("d.txt", "d\n"));
        await box.Updates.GetBasisHeadsAsync(refresh: true);

        var second = await box.Updates.CheckAsync(project);
        Assert.Equal(BasisUpdateStatus.UpdateAvailable, second.Status);
        Assert.Equal(tip, second.RemoteSha);
        Assert.Equal(2, second.Behind);
    }

    [GitFact]
    public async Task Fast_forward_update_moves_the_project_to_the_latest_basis()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        var tip = box.CommitUpstream("upstream work", ("a.txt", "ONE\ntwo\nthree\n"), ("new.txt", "new\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.FastForward, plan.Kind);
        Assert.Equal(1, plan.IncomingCount);
        Assert.Equal("upstream work", Assert.Single(plan.IncomingCommits).Subject);
        Assert.Empty(plan.CollidingPaths);

        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateResultKind.Updated, result.Kind);
        Assert.Equal(tip, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal("new\n", GitSandbox.Read(project, "new.txt"));
        Assert.Null(await box.Updates.LoadStateAsync(project));
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Local_edits_survive_and_edits_basis_also_touched_are_merged_back()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Write(project, "a.txt", "one\ntwo\nthree LOCAL\n");
        GitSandbox.Write(project, "b.txt", "b LOCAL\n");
        box.CommitUpstream("upstream", ("a.txt", "ONE\ntwo\nthree\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.FastForward, plan.Kind);
        Assert.Equal(2, plan.UncommittedCount);
        Assert.Equal(new[] { "a.txt" }, plan.CollidingPaths);

        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateResultKind.Updated, result.Kind);
        Assert.Equal("ONE\ntwo\nthree LOCAL\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("b LOCAL\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal(" M a.txt\n M b.txt", GitSandbox.Status(project));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
        Assert.Null(await box.Git.ResolveCommitAsync(project, BasisUpdateService.SetAsideRef));
    }

    [GitFact]
    public async Task Overlapping_local_edit_waits_for_a_choice_and_keep_mine_finishes()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Write(project, "a.txt", "MINE\ntwo\nthree\n");
        var tip = box.CommitUpstream("upstream", ("a.txt", "BASIS\ntwo\nthree\n"));

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));

        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdatePhase.RestoringChanges, result.Phase);
        var conflict = Assert.Single(result.Conflicts!);
        Assert.Equal("a.txt", conflict.Path);
        Assert.Equal(ConflictKind.BothChanged, conflict.Kind);
        Assert.Equal(BasisUpdateKind.InProgress, (await box.Updates.PlanAsync(project)).Kind);
        Assert.True((await box.Updates.CheckAsync(project)).InProgress);

        Assert.True((await box.Updates.ResolveAsync(project, "a.txt", ConflictChoice.Mine)).Ok);
        var finished = await box.Updates.ContinueAsync(project);

        Assert.Equal(BasisUpdateResultKind.Updated, finished.Kind);
        Assert.Equal(tip, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal("MINE\ntwo\nthree\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal(" M a.txt", GitSandbox.Status(project));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
        Assert.Null(await box.Updates.LoadStateAsync(project));
    }

    [GitFact]
    public async Task Choosing_basis_for_an_overlapping_edit_takes_the_new_basis_file()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Write(project, "a.txt", "MINE\ntwo\nthree\n");
        box.CommitUpstream("upstream", ("a.txt", "BASIS\ntwo\nthree\n"));

        await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.True((await box.Updates.ResolveAsync(project, "a.txt", ConflictChoice.Basis)).Ok);
        var finished = await box.Updates.ContinueAsync(project);

        Assert.Equal(BasisUpdateResultKind.Updated, finished.Kind);
        Assert.Equal("BASIS\ntwo\nthree\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Committed_conflict_is_settled_with_the_basis_version_and_a_merge_commit_is_recorded()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Commit(project, "my change", ("c.txt", "mine\n"));
        box.CommitUpstream("upstream", ("c.txt", "basis\n"), ("added.txt", "added\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Merge, plan.Kind);
        Assert.Equal(1, plan.LocalCommitCount);
        if (plan.PredictedConflicts is not null) Assert.Equal(new[] { "c.txt" }, plan.PredictedConflicts);

        var result = await box.Updates.ApplyAsync(project, plan);
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdatePhase.Merging, result.Phase);
        Assert.Equal(ConflictKind.BothChanged, Assert.Single(result.Conflicts!).Kind);

        var early = await box.Updates.ContinueAsync(project);
        Assert.Equal(BasisUpdateResultKind.Conflicts, early.Kind);

        Assert.True((await box.Updates.ResolveAsync(project, "c.txt", ConflictChoice.Basis)).Ok);
        var finished = await box.Updates.ContinueAsync(project);

        Assert.Equal(BasisUpdateResultKind.Updated, finished.Kind);
        Assert.Equal("basis\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("added\n", GitSandbox.Read(project, "added.txt"));
        Assert.Equal(2, GitSandbox.Run(project, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1);
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Abort_during_the_merge_puts_everything_back_exactly()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        var original = GitSandbox.Commit(project, "my change", ("c.txt", "mine\n"));
        GitSandbox.Write(project, "a.txt", "one\ntwo\nthree LOCAL\n");
        GitSandbox.Write(project, "b.txt", "b LOCAL\n");
        GitSandbox.Write(project, "fresh.txt", "my fresh file\n");
        box.CommitUpstream("upstream", ("c.txt", "basis\n"), ("a.txt", "ONE\ntwo\nthree\n"), ("fresh.txt", "basis fresh\n"));
        var before = GitSandbox.Status(project);

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdatePhase.Merging, result.Phase);

        var aborted = await box.Updates.AbortAsync(project);

        Assert.Equal(BasisUpdateResultKind.Aborted, aborted.Kind);
        Assert.Equal(original, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal(before, GitSandbox.Status(project));
        Assert.Equal("one\ntwo\nthree LOCAL\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("b LOCAL\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal("my fresh file\n", GitSandbox.Read(project, "fresh.txt"));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
        Assert.Null(await box.Updates.LoadStateAsync(project));
    }

    [GitFact]
    public async Task Abort_while_putting_edits_back_rolls_the_update_back()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        var original = GitSandbox.Run(project, "rev-parse", "HEAD");
        GitSandbox.Write(project, "a.txt", "MINE\ntwo\nthree\n");
        GitSandbox.Write(project, "b.txt", "b LOCAL\n");
        box.CommitUpstream("upstream", ("a.txt", "BASIS\ntwo\nthree\n"));
        var before = GitSandbox.Status(project);

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdatePhase.RestoringChanges, result.Phase);

        var aborted = await box.Updates.AbortAsync(project);

        Assert.Equal(BasisUpdateResultKind.Aborted, aborted.Kind);
        Assert.Equal(original, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal(before, GitSandbox.Status(project));
        Assert.Equal("MINE\ntwo\nthree\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
    }

    [GitFact]
    public async Task New_file_that_basis_also_adds_becomes_a_both_added_choice()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Write(project, "new.txt", "mine\n");
        box.CommitUpstream("upstream", ("new.txt", "basis\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(new[] { "new.txt" }, plan.CollidingPaths);
        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdatePhase.RestoringChanges, result.Phase);
        Assert.Equal(ConflictKind.BothAdded, Assert.Single(result.Conflicts!).Kind);
        Assert.True((await box.Updates.ResolveAsync(project, "new.txt", ConflictChoice.Mine)).Ok);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);
        Assert.Equal("mine\n", GitSandbox.Read(project, "new.txt"));
        Assert.Equal(" M new.txt", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Ignored_files_in_the_way_block_the_update_before_anything_changes()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        var original = GitSandbox.Run(project, "rev-parse", "HEAD");
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "\nPackages/com.example/\n");
        GitSandbox.Write(project, "Packages/com.example/package.json", "{ \"mounted\": true }\n");
        box.CommitUpstream("embed package", ("Packages/com.example/package.json", "{ \"basis\": true }\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.False(plan.CanApply);
        Assert.Equal(new[] { "Packages/com.example/package.json" }, plan.BlockingPaths);

        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateFailure.IgnoredFilesInTheWay, result.Failure);
        Assert.Equal(original, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal("{ \"mounted\": true }\n", GitSandbox.Read(project, "Packages/com.example/package.json"));
    }

    [GitFact]
    public async Task Ignored_meta_files_in_the_way_are_swapped_for_basis_copies()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "\nAssets/Tools.meta\n");
        GitSandbox.Write(project, "Assets/Tools.meta", "guid: mine\n");
        box.CommitUpstream("add tools", ("Assets/Tools.meta", "guid: basis\n"), ("Assets/Tools/readme.txt", "tools\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.True(plan.CanApply);
        Assert.Equal(new[] { "Assets/Tools.meta" }, plan.ReplacedMetaPaths);

        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateResultKind.Updated, result.Kind);
        Assert.Equal("guid: basis\n", GitSandbox.Read(project, "Assets/Tools.meta"));
        Assert.False(Directory.Exists(Path.Combine(project, ".git", "basispm-discarded")));
    }

    [GitFact]
    public async Task Undoing_the_update_puts_discarded_meta_files_back()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Commit(project, "my change", ("c.txt", "mine\n"));
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "\nAssets/Tools.meta\n");
        GitSandbox.Write(project, "Assets/Tools.meta", "guid: mine\n");
        box.CommitUpstream("upstream", ("c.txt", "basis\n"), ("Assets/Tools.meta", "guid: basis\n"));

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);

        Assert.Equal(BasisUpdateResultKind.Aborted, (await box.Updates.AbortAsync(project)).Kind);
        Assert.Equal("guid: mine\n", GitSandbox.Read(project, "Assets/Tools.meta"));
        Assert.False(Directory.Exists(Path.Combine(project, ".git", "basispm-discarded")));
    }

    [GitFact]
    public async Task Other_ignored_files_still_block_and_leave_meta_files_alone()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "\nPackages/com.example/\nPackages/com.example.meta\n");
        GitSandbox.Write(project, "Packages/com.example/package.json", "{ \"mounted\": true }\n");
        GitSandbox.Write(project, "Packages/com.example.meta", "guid: mine\n");
        box.CommitUpstream("embed package", ("Packages/com.example/package.json", "{ \"basis\": true }\n"), ("Packages/com.example.meta", "guid: basis\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(new[] { "Packages/com.example/package.json" }, plan.BlockingPaths);

        Assert.Equal(BasisUpdateFailure.IgnoredFilesInTheWay, (await box.Updates.ApplyAsync(project, plan)).Failure);
        Assert.Equal("guid: mine\n", GitSandbox.Read(project, "Packages/com.example.meta"));
    }

    [GitFact]
    public async Task Staged_changes_are_set_aside_so_a_real_merge_can_run()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Commit(project, "my change", ("mine.txt", "mine\n"));
        GitSandbox.Write(project, "b.txt", "b STAGED\n");
        GitSandbox.Run(project, "add", "b.txt");
        box.CommitUpstream("upstream", ("c.txt", "basis\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Merge, plan.Kind);
        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateResultKind.Updated, result.Kind);
        Assert.Equal("b STAGED\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal("basis\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("M  b.txt", GitSandbox.Status(project));
        Assert.NotEqual("b STAGED", GitSandbox.Run(project, "show", "HEAD:b.txt"));
    }

    [GitFact]
    public async Task Marked_resolved_is_refused_while_conflict_markers_remain()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Commit(project, "my change", ("c.txt", "mine\n"));
        box.CommitUpstream("upstream", ("c.txt", "basis\n"));
        await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));

        var refused = await box.Updates.ResolveAsync(project, "c.txt", ConflictChoice.Resolved);
        Assert.Equal(BasisUpdateFailure.MarkersRemain, refused.Failure);

        GitSandbox.Write(project, "c.txt", "mine and basis\n");
        Assert.True((await box.Updates.ResolveAsync(project, "c.txt", ConflictChoice.Resolved)).Ok);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);
        Assert.Equal("mine and basis\n", GitSandbox.Read(project, "c.txt"));
    }

    [GitFact]
    public async Task Unrelated_history_can_still_be_linked_to_the_matching_basis_version_then_merged()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c1\n"), ("d.txt", "d1\n"));
        box.CommitUpstream("two", ("a.txt", "a2\n"), ("e.txt", "e2\n"));
        var three = box.CommitUpstream("three", ("b.txt", "b3\n"), ("f.txt", "f3\n"));
        box.CommitUpstream("four", ("c.txt", "c4\n"), ("g.txt", "g4\n"));
        box.CommitUpstream("five", ("d.txt", "d5\n"), ("e.txt", null));

        var project = box.Combine("standalone");
        Directory.CreateDirectory(project);
        foreach (var file in new[] { "a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt" })
            GitSandbox.Write(project, file, GitSandbox.Run(box.Upstream, "show", $"{three}:{file}") + "\n");
        GitSandbox.Write(project, "c.txt", "c1 EDITED\n");
        GitSandbox.Write(project, "game.txt", "my game\n");
        GitSandbox.Run(project, "init", "-q", "-b", "main");
        GitSandbox.Run(project, "config", "core.autocrlf", "false");
        GitSandbox.Commit(project, "import");

        var plan = await box.Updates.PlanAsync(project, "developer");
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.Equal(BasisBaseSource.Similarity, plan.BaseSource);
        Assert.False(plan.Connected);
        Assert.Equal(three, plan.SuggestedBase?.Sha);

        Assert.True((await box.Updates.LinkAsync(project, plan.SuggestedBase!.Sha)).Ok);
        Assert.Equal("", GitSandbox.Status(project));
        var linked = await box.Updates.PlanAsync(project, "developer");
        Assert.Equal(BasisUpdateKind.Merge, linked.Kind);
        Assert.Equal(2, linked.IncomingCount);

        var result = await box.Updates.ApplyAsync(project, linked);
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal("c.txt", Assert.Single(result.Conflicts!).Path);
        Assert.True((await box.Updates.ResolveAsync(project, "c.txt", ConflictChoice.Mine)).Ok);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);

        Assert.Equal("c1 EDITED\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("d5\n", GitSandbox.Read(project, "d.txt"));
        Assert.Equal("g4\n", GitSandbox.Read(project, "g.txt"));
        Assert.Equal("my game\n", GitSandbox.Read(project, "game.txt"));
        Assert.False(File.Exists(Path.Combine(project, "e.txt")));
    }

    [GitFact]
    public async Task Project_without_git_is_recorded_then_linked()
    {
        using var box = new GitSandbox();
        var base1 = box.CommitUpstream("one", ("Basis/Assets/a.txt", "a1\n"), ("Basis/.gitignore", "/[Ll]ibrary/\n"), ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n"));
        box.CommitUpstream("two", ("Basis/Assets/a.txt", "a2\n"));

        var project = box.Combine("zip");
        foreach (var file in new[] { "Basis/Assets/a.txt", "Basis/.gitignore", "Basis/ProjectSettings/ProjectVersion.txt" })
            GitSandbox.Write(project, file, GitSandbox.Run(box.Upstream, "show", $"{base1}:{file}") + "\n");
        GitSandbox.Write(project, "Basis/Library/huge.bin", "cache");

        Assert.Equal(BasisUpdateKind.NotGitRepo, (await box.Updates.PlanAsync(project)).Kind);
        var init = await box.Updates.InitializeRepositoryAsync(project, Path.Combine(project, "Basis"));
        Assert.True(init.Ok, init.Detail);
        Assert.DoesNotContain("Library", GitSandbox.Run(project, "ls-files"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.Equal(base1, plan.SuggestedBase?.Sha);
        Assert.Equal(base1, plan.MergeBase);
    }

    [GitFact]
    public async Task Project_without_git_gets_basis_git_rules_before_its_first_commit()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("Assets/a.txt", "a1\n"));
        var project = box.Combine("zip");
        GitSandbox.Write(project, "Assets/a.txt", "a1\n");
        foreach (var junk in new[] { "Library/huge.bin", "Temp/lock", "Logs/Editor.log", "UserSettings/Layouts.dwlt", ".vs/cache", "Assembly-CSharp.csproj", "Game.sln" })
            GitSandbox.Write(project, junk, "generated");

        var init = await box.Updates.InitializeRepositoryAsync(project, project);

        Assert.True(init.Ok, init.Detail);
        Assert.Equal(new[] { ".gitignore", ".gitattributes" }, init.AddedFiles);
        Assert.Equal(".gitattributes\n.gitignore\nAssets/a.txt", GitSandbox.Run(project, "ls-files"));
        Assert.Equal(BasisGitDefaults.UnityIgnore, File.ReadAllText(Path.Combine(project, ".gitignore")));
        if (OperatingSystem.IsWindows()) Assert.Equal("true", GitSandbox.Run(project, "config", "core.longpaths"));
    }

    [GitFact]
    public async Task Project_gitignore_that_lets_library_through_gets_basis_rules_appended()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("Assets/a.txt", "a1\n"));
        var project = box.Combine("zip");
        GitSandbox.Write(project, "Assets/a.txt", "a1\n");
        GitSandbox.Write(project, ".gitignore", "*.tmp");
        GitSandbox.Write(project, "Library/huge.bin", "cache");

        var init = await box.Updates.InitializeRepositoryAsync(project, project);

        Assert.True(init.Ok, init.Detail);
        Assert.Contains(".gitignore", init.AddedFiles);
        Assert.StartsWith("*.tmp\n\n# Unity's generated files", File.ReadAllText(Path.Combine(project, ".gitignore")));
        Assert.DoesNotContain("Library", GitSandbox.Run(project, "ls-files"));
    }

    [GitFact]
    public async Task Package_cloned_with_its_own_git_is_left_out_of_the_first_commit()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("Assets/a.txt", "a1\n"));
        var project = box.Combine("zip");
        GitSandbox.Write(project, "Assets/a.txt", "a1\n");
        var package = Path.Combine(project, "Packages", "com.vendor.lights");
        GitSandbox.Write(package, "package.json", "{}");
        GitSandbox.Run(package, "init", "-q");
        GitSandbox.Commit(package, "vendor");

        var init = await box.Updates.InitializeRepositoryAsync(project, project);

        Assert.True(init.Ok, init.Detail);
        Assert.Equal(new[] { "Packages/com.vendor.lights" }, init.LeftOut);
        Assert.DoesNotContain("com.vendor.lights", GitSandbox.Run(project, "ls-files"));
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task VRChat_style_packages_gitignore_stops_hiding_the_projects_packages()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("Assets/a.txt", "a1\n"));
        var project = box.Combine("zip");
        GitSandbox.Write(project, "Assets/a.txt", "a1\n");
        GitSandbox.Write(project, "Packages/.gitignore", "/*/\ncom.vrchat.*/\n!com.vrchat.core.*/\n!vpm-manifest.json\n!manifest.json\n");
        GitSandbox.Write(project, "Packages/manifest.json", "{}");
        GitSandbox.Write(project, "Packages/com.basis.sdk/package.json", "{}");
        GitSandbox.Write(project, "Packages/com.basis.sdk/Runtime/Sdk.cs", "class Sdk {}");
        GitSandbox.Write(project, "Packages/com.vrchat.base/package.json", "{}");
        GitSandbox.Write(project, "Packages/Server Export/build.dll", "binary");
        var clone = Path.Combine(project, "Packages", "com.vendor.lights");
        GitSandbox.Write(clone, "package.json", "{}");
        GitSandbox.Run(clone, "init", "-q");
        GitSandbox.Commit(clone, "vendor");

        var init = await box.Updates.InitializeRepositoryAsync(project, project);

        Assert.True(init.Ok, init.Detail);
        Assert.Equal(new[] { "Packages/com.basis.sdk", "Packages/com.vrchat.base" }, init.Unhidden.Order());
        Assert.Equal(new[] { "Packages/com.vendor.lights" }, init.LeftOut);
        var tracked = GitSandbox.Run(project, "ls-files");
        Assert.Contains("Packages/com.basis.sdk/Runtime/Sdk.cs", tracked);
        Assert.Contains("Packages/com.vrchat.base/package.json", tracked);
        Assert.Contains("Packages/manifest.json", tracked);
        Assert.DoesNotContain("Server Export", tracked);
        Assert.DoesNotContain("com.vendor.lights", tracked);
        Assert.EndsWith("!/com.basis.sdk/\n!/com.vrchat.base/\n", GitSandbox.Read(project, "Packages/.gitignore").Replace("\r", ""));
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Basis_branch_follows_the_saved_choice_then_tracking_then_developer()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        GitSandbox.Run(box.Upstream, "branch", "long-term-support-20260101");
        GitSandbox.Run(project, "fetch", "-q", "origin");
        var heads = await box.Updates.GetBasisHeadsAsync(refresh: true);

        Assert.Equal("developer", await box.Updates.ResolveBasisBranchAsync(project, heads));

        GitSandbox.Run(project, "checkout", "-q", "-b", "long-term-support-20260101", "--track", "origin/long-term-support-20260101");
        Assert.Equal("long-term-support-20260101", await box.Updates.ResolveBasisBranchAsync(project, heads));

        GitSandbox.Run(project, "checkout", "-q", "-b", "my-game");
        Assert.Equal("developer", await box.Updates.ResolveBasisBranchAsync(project, heads));

        Assert.True((await box.Updates.SetBasisBranchAsync(project, "long-term-support-20260101")).Ok);
        Assert.Equal("long-term-support-20260101", await box.Updates.ResolveBasisBranchAsync(project, heads));
    }

    [GitFact]
    public async Task Detached_head_and_a_merge_already_in_progress_block_the_update()
    {
        var (box, project) = StartWithClone();
        using var _ = box;
        box.CommitUpstream("upstream", ("c.txt", "basis\n"));

        GitSandbox.Run(project, "checkout", "-q", "--detach");
        var detached = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateBlock.DetachedHead, detached.Block);

        GitSandbox.Run(project, "checkout", "-q", "developer");
        GitSandbox.Run(project, "checkout", "-q", "-b", "other");
        GitSandbox.Commit(project, "other change", ("c.txt", "other\n"));
        GitSandbox.Run(project, "checkout", "-q", "developer");
        GitSandbox.Commit(project, "dev change", ("c.txt", "dev\n"));
        Assert.Equal(1, GitSandbox.RunExitCode(project, "merge", "other"));

        var busy = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateBlock.OperationInProgress, busy.Block);
        Assert.Equal("merge", busy.Detail);
    }

    [GitFact]
    public async Task Project_registered_at_a_subfolder_updates_the_enclosing_repository()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n"), ("Basis/Assets/a.txt", "a\n"));
        var clone = box.CloneProject();
        var tip = box.CommitUpstream("upstream", ("Basis/Assets/a.txt", "A\n"));
        var unityFolder = Path.Combine(clone, "Basis");

        var plan = await box.Updates.PlanAsync(unityFolder);
        Assert.Equal(BasisUpdateKind.FastForward, plan.Kind);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(unityFolder, plan)).Kind);
        Assert.Equal(tip, GitSandbox.Run(clone, "rev-parse", "HEAD"));
    }

    [Fact]
    public void Branches_are_ordered_developer_then_newest_long_term_support()
    {
        var ordered = BasisUpdateService.OrderBranches(new[]
        {
            "zeta", "long-term-support-20251102", "developer", "alpha", "long-term-support-20260916",
        });
        Assert.Equal(new[] { "developer", "long-term-support-20260916", "long-term-support-20251102", "alpha", "zeta" }, ordered);
    }

    [Theory]
    [InlineData("https://github.com/BasisVR/Basis.git")]
    [InlineData("https://github.com/BasisVR/Basis")]
    [InlineData("https://github.com/basisvr/basis/")]
    [InlineData("git@github.com:BasisVR/Basis.git")]
    [InlineData("ssh://git@github.com/BasisVR/Basis.git")]
    [InlineData("https://www.github.com/BasisVR/Basis")]
    public void Same_repository_ignores_url_spelling(string url) =>
        Assert.True(BasisUpdateService.IsSameRepository(url, BasisInstallService.BasisRepoUrl));

    [Theory]
    [InlineData("https://github.com/SomeoneElse/Basis.git")]
    [InlineData("https://gitlab.com/BasisVR/Basis.git")]
    [InlineData("https://github.com/BasisVR/BasisPackageManager.git")]
    public void Different_repositories_are_not_the_same(string url) =>
        Assert.False(BasisUpdateService.IsSameRepository(url, BasisInstallService.BasisRepoUrl));

    [Fact]
    public void Conflict_markers_are_detected_in_text_but_not_binary_files()
    {
        using var t = new TempDir();
        var marked = t.WriteFile("marked.txt", "a\n<<<<<<< HEAD\nx\n=======\ny\n>>>>>>> basis\n");
        var clean = t.WriteFile("clean.md", "Title\n=======\nbody\n");
        var binary = t.Combine("blob.bin");
        File.WriteAllBytes(binary, new byte[] { 0, 1, 2, (byte)'<', (byte)'<', (byte)'<' });

        Assert.True(BasisUpdateService.HasConflictMarkers(marked));
        Assert.False(BasisUpdateService.HasConflictMarkers(clean));
        Assert.False(BasisUpdateService.HasConflictMarkers(binary));
    }

    [Theory]
    [InlineData("Assets/Scenes/Main.unity", true)]
    [InlineData("Assets/Prefabs/Thing.prefab", true)]
    [InlineData("Assets/Thing.cs.meta", true)]
    [InlineData("Assets/Scripts/Thing.cs", false)]
    public void Unity_assets_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, BasisUpdateService.IsUnityAsset(path));
}
