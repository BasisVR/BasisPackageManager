using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class RehostedProjectTests
{
    private const string Lts = "long-term-support-20260101";

    private static string Message(string repo) => GitSandbox.Run(repo, "log", "-1", "--format=%B");

    private static int ParentCount(string repo) => GitSandbox.Run(repo, "rev-list", "--parents", "-n", "1", "HEAD").Split(' ').Length - 1;

    [GitFact]
    public async Task Clone_moved_to_its_own_remote_still_updates_straight_from_basis()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", "a\n"), ("b.txt", "b\n"));
        var project = box.CloneProject();
        var studio = box.CreateBareRepository("studio.git");
        GitSandbox.Run(project, "remote", "set-url", "origin", studio);
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "developer");
        GitSandbox.Commit(project, "my game", ("game.txt", "game\n"));
        GitSandbox.Run(project, "push", "-q");
        var tip = box.CommitUpstream("basis work", ("a.txt", "A\n"));
        await box.Updates.GetBasisHeadsAsync(refresh: true);

        Assert.Equal(BasisUpdateStatus.UpdateAvailable, (await box.Updates.CheckAsync(project)).Status);
        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Merge, plan.Kind);
        Assert.Equal("developer", plan.BasisBranch);
        Assert.False(plan.IsSwitch);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("A\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("game\n", GitSandbox.Read(project, "game.txt"));
        Assert.True(await box.Git.IsAncestorAsync(project, tip, "HEAD"));
        Assert.Contains($"{BasisUpdateService.CommitTrailer}: {tip}", Message(project));
        Assert.Equal(studio, GitSandbox.Run(project, "remote", "get-url", "origin"));
        Assert.Equal("origin/developer", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "@{u}"));
        Assert.NotEqual(GitSandbox.Run(project, "rev-parse", "HEAD"), GitSandbox.Run(studio, "rev-parse", "developer"));
        Assert.Equal("", GitSandbox.Status(project));
        Assert.Equal(BasisUpdateStatus.UpToDate, (await box.Updates.CheckAsync(project)).Status);
    }

    [GitFact]
    public async Task Copied_project_takes_basis_updates_without_basis_history()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c1\n"), ("d.txt", "d1\n"));
        var two = box.CommitUpstream("two", ("a.txt", "a2\n"), ("e.txt", "e2\n"));
        var project = box.CopyProject(two, "copy");
        GitSandbox.Commit(project, "my changes", ("c.txt", "c MINE\n"), ("game.txt", "my game\n"));
        GitSandbox.Write(project, "b.txt", "b uncommitted\n");
        var three = box.CommitUpstream("three", ("c.txt", "c3\n"), ("d.txt", "d3\n"), ("f.txt", "f3\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.Equal(BasisBaseSource.Similarity, plan.BaseSource);
        Assert.False(plan.Connected);
        Assert.Equal(two, plan.MergeBase);
        Assert.Equal(1, plan.IncomingCount);
        if (plan.PredictedConflicts is not null) Assert.Equal(new[] { "c.txt" }, plan.PredictedConflicts);

        var result = await box.Updates.ApplyAsync(project, plan);
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdatePhase.Merging, result.Phase);
        Assert.Equal("c.txt", Assert.Single(result.Conflicts!).Path);
        Assert.True((await box.Updates.ResolveAsync(project, "c.txt", ConflictChoice.Mine)).Ok);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);

        Assert.Equal("c MINE\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("d3\n", GitSandbox.Read(project, "d.txt"));
        Assert.Equal("f3\n", GitSandbox.Read(project, "f.txt"));
        Assert.Equal("my game\n", GitSandbox.Read(project, "game.txt"));
        Assert.Equal("b uncommitted\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal(" M b.txt", GitSandbox.Status(project));
        Assert.Equal("3", GitSandbox.Run(project, "rev-list", "--count", "HEAD"));
        Assert.Equal(1, ParentCount(project));
        Assert.Contains($"{BasisUpdateService.CommitTrailer}: {three}", Message(project));
        Assert.Equal(1, GitSandbox.RunExitCode(project, "merge-base", "HEAD", three));
        Assert.False(File.Exists(Path.Combine(project, ".git", "MERGE_MSG")));

        GitSandbox.Commit(project, "more work", ("game.txt", "my game 2\n"));
        box.CommitUpstream("four", ("a.txt", "a4\n"));
        await box.Updates.GetBasisHeadsAsync(refresh: true);
        var second = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Apply, second.Kind);
        Assert.Equal(BasisBaseSource.Recorded, second.BaseSource);
        Assert.Equal(three, second.MergeBase);
        Assert.Equal(1, second.IncomingCount);
        Assert.Equal(1, second.LocalCommitCount);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, second)).Kind);
        Assert.Equal("a4\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("my game 2\n", GitSandbox.Read(project, "game.txt"));
        Assert.Equal("5", GitSandbox.Run(project, "rev-list", "--count", "HEAD"));
        Assert.Equal(BasisUpdateStatus.UpToDate, (await box.Updates.CheckAsync(project)).Status);
    }

    [GitFact]
    public async Task Project_made_from_the_basis_unity_folder_gets_only_unity_changes()
    {
        using var box = new GitSandbox();
        var start = box.CommitUpstream("start",
            ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n"),
            ("Basis/Assets/a.txt", "a1\n"),
            ("Basis/Assets/b.txt", "b1\n"),
            ("Basis/Packages/manifest.json", "{}\n"),
            ("Basis Server/server.txt", "s1\n"),
            ("README.md", "readme\n"));
        var project = box.CopyProject(start, "unity-only", BasisUpdateService.BasisUnityFolder);
        GitSandbox.Commit(project, "my game", ("Assets/game.txt", "game\n"));
        box.CommitUpstream("update", ("Basis/Assets/a.txt", "a2\n"), ("Basis Server/server.txt", "s2\n"), ("README.md", "readme 2\n"));
        var tip = box.CommitUpstream("server only", ("Basis Server/server.txt", "s3\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.Equal(new BasisLayout("Basis", ""), plan.Layout);
        Assert.Equal(start, plan.MergeBase);
        Assert.Equal(1, plan.IncomingCount);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("a2\n", GitSandbox.Read(project, "Assets/a.txt"));
        Assert.Equal("game\n", GitSandbox.Read(project, "Assets/game.txt"));
        Assert.False(Directory.Exists(Path.Combine(project, "Basis Server")));
        Assert.False(Directory.Exists(Path.Combine(project, "Basis")));
        Assert.False(File.Exists(Path.Combine(project, "README.md")));
        Assert.Contains($"{BasisUpdateService.FolderTrailer}: Basis -> .", Message(project));

        var recorded = await box.Updates.ResolveBasisBaseAsync(project);
        Assert.Equal(tip, recorded?.Sha);
        Assert.Equal(BasisBaseSource.Recorded, recorded?.Source);
        Assert.Equal(new BasisLayout("Basis", ""), recorded?.Layout);
    }

    [GitFact]
    public async Task Rehosted_long_term_support_project_keeps_following_its_branch()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"));
        box.CommitUpstream("two", ("a.txt", "a2\n"));
        GitSandbox.Run(box.Upstream, "branch", Lts);
        box.CommitUpstream("three", ("b.txt", "b3\n"));
        var project = box.CloneProject("project", Lts);
        var studio = box.CreateBareRepository("studio.git");
        GitSandbox.Run(project, "branch", "-m", "main");
        GitSandbox.Run(project, "remote", "set-url", "origin", studio);
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "main");
        GitSandbox.Commit(project, "my game", ("game.txt", "game\n"));

        var current = await box.Updates.PlanAsync(project);
        Assert.Equal(Lts, current.BasisBranch);
        Assert.Equal(BasisUpdateKind.UpToDate, current.Kind);

        GitSandbox.Run(box.Upstream, "checkout", "-q", Lts);
        box.CommitUpstream("lts fix", ("a.txt", "a2 fixed\n"));
        GitSandbox.Run(box.Upstream, "checkout", "-q", "developer");
        await box.Updates.GetBasisHeadsAsync(refresh: true);

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(Lts, plan.BasisBranch);
        Assert.Equal(BasisUpdateKind.Merge, plan.Kind);
        Assert.Equal(1, plan.IncomingCount);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("a2 fixed\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("b1\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal(Lts, await box.Updates.ResolveBasisBranchAsync(project));
    }

    [GitFact]
    public async Task Switching_to_an_older_basis_branch_keeps_your_commits_and_edits()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("z.txt", "z1\n"));
        var two = box.CommitUpstream("two", ("a.txt", "a2\n"));
        GitSandbox.Run(box.Upstream, "branch", Lts);
        box.CommitUpstream("three", ("b.txt", "b3\n"));
        var four = box.CommitUpstream("four", ("y.txt", "y4\n"));
        var project = box.CloneProject();
        GitSandbox.Commit(project, "my change", ("z.txt", "z MINE\n"));
        GitSandbox.Write(project, "w.txt", "uncommitted\n");

        var plan = await box.Updates.PlanAsync(project, Lts);
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.True(plan.IsSwitch);
        Assert.Equal("developer", plan.FromBranch);
        Assert.Equal(four, plan.MergeBase);
        Assert.Equal(2, plan.OutgoingCount);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("a2\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("b1\n", GitSandbox.Read(project, "b.txt"));
        Assert.False(File.Exists(Path.Combine(project, "y.txt")));
        Assert.Equal("z MINE\n", GitSandbox.Read(project, "z.txt"));
        Assert.Equal("uncommitted\n", GitSandbox.Read(project, "w.txt"));
        Assert.Equal("?? w.txt", GitSandbox.Status(project));
        Assert.Contains($"{BasisUpdateService.BranchTrailer}: {Lts}", Message(project));
        Assert.Equal(Lts, await box.Updates.ResolveBasisBranchAsync(project));
        Assert.Equal(BasisUpdateStatus.UpToDate, (await box.Updates.CheckAsync(project)).Status);

        GitSandbox.Run(box.Upstream, "branch", "-f", Lts, four);
        await box.Updates.GetBasisHeadsAsync(refresh: true);
        var forward = await box.Updates.PlanAsync(project);
        Assert.Equal(Lts, forward.BasisBranch);
        Assert.Equal(BasisUpdateKind.Apply, forward.Kind);
        Assert.Equal(two, forward.MergeBase);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, forward)).Kind);
        Assert.Equal("b3\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal("y4\n", GitSandbox.Read(project, "y.txt"));
        Assert.Equal("z MINE\n", GitSandbox.Read(project, "z.txt"));
        Assert.Equal(BasisUpdateStatus.UpToDate, (await box.Updates.CheckAsync(project)).Status);
    }

    [GitFact]
    public async Task Switching_to_a_newer_basis_branch_records_the_switch()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"));
        var project = box.CloneProject();
        GitSandbox.Run(box.Upstream, "checkout", "-q", "-b", Lts);
        var lts = box.CommitUpstream("lts only", ("b.txt", "lts\n"));
        GitSandbox.Run(box.Upstream, "checkout", "-q", "developer");

        var plan = await box.Updates.PlanAsync(project, Lts);
        Assert.Equal(BasisUpdateKind.Merge, plan.Kind);
        Assert.True(plan.IsSwitch);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("lts\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal(2, ParentCount(project));
        Assert.Contains($"{BasisUpdateService.CommitTrailer}: {lts}", Message(project));
        Assert.Equal(Lts, await box.Updates.ResolveBasisBranchAsync(project));
    }

    [GitFact]
    public async Task Copied_project_can_switch_basis_branch()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c1\n"));
        GitSandbox.Run(box.Upstream, "branch", Lts);
        var two = box.CommitUpstream("two", ("b.txt", "b2\n"), ("d.txt", "d2\n"));
        var project = box.CopyProject(two, "copy");
        GitSandbox.Commit(project, "mine", ("game.txt", "game\n"), ("c.txt", "c MINE\n"));

        var plan = await box.Updates.PlanAsync(project, Lts);
        Assert.Equal(BasisUpdateKind.Apply, plan.Kind);
        Assert.True(plan.IsSwitch);
        Assert.Equal("developer", plan.FromBranch);
        Assert.False(plan.Connected);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ApplyAsync(project, plan)).Kind);
        Assert.Equal("b1\n", GitSandbox.Read(project, "b.txt"));
        Assert.False(File.Exists(Path.Combine(project, "d.txt")));
        Assert.Equal("c MINE\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("game\n", GitSandbox.Read(project, "game.txt"));
        Assert.Equal(1, ParentCount(project));
        Assert.Equal(Lts, await box.Updates.ResolveBasisBranchAsync(project));
    }

    [GitFact]
    public async Task Undoing_an_update_of_a_copied_project_restores_everything()
    {
        using var box = new GitSandbox();
        var one = box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c1\n"));
        var project = box.CopyProject(one, "copy");
        var original = GitSandbox.Commit(project, "mine", ("c.txt", "c MINE\n"));
        GitSandbox.Write(project, "a.txt", "a uncommitted\n");
        GitSandbox.Write(project, "f.txt", "my f\n");
        box.CommitUpstream("two", ("a.txt", "a2\n"), ("c.txt", "c2\n"), ("f.txt", "basis f\n"));
        var before = GitSandbox.Status(project);

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal("c.txt", Assert.Single(result.Conflicts!).Path);

        var aborted = await box.Updates.AbortAsync(project);
        Assert.Equal(BasisUpdateResultKind.Aborted, aborted.Kind);
        Assert.Equal(original, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal(before, GitSandbox.Status(project));
        Assert.Equal("a uncommitted\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("c MINE\n", GitSandbox.Read(project, "c.txt"));
        Assert.Equal("my f\n", GitSandbox.Read(project, "f.txt"));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
        Assert.False(File.Exists(Path.Combine(project, ".git", "MERGE_MSG")));
        Assert.Null(await box.Updates.LoadStateAsync(project));
    }

    [GitFact]
    public async Task Finishing_an_update_with_another_git_tool_keeps_the_basis_record()
    {
        using var box = new GitSandbox();
        var one = box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"), ("c.txt", "c1\n"));
        var project = box.CopyProject(one, "copy");
        GitSandbox.Commit(project, "mine", ("c.txt", "c MINE\n"));
        var two = box.CommitUpstream("two", ("a.txt", "a2\n"), ("c.txt", "c2\n"));

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Contains($"{BasisUpdateService.CommitTrailer}: {two}", File.ReadAllText(Path.Combine(project, ".git", "MERGE_MSG")));

        GitSandbox.Write(project, "c.txt", "c MINE\n");
        GitSandbox.Run(project, "add", "c.txt");
        GitSandbox.Run(project, "commit", "-q", "--no-edit");
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);

        Assert.Contains($"{BasisUpdateService.CommitTrailer}: {two}", Message(project));
        Assert.Equal(two, (await box.Updates.ResolveBasisBaseAsync(project))?.Sha);
        Assert.Equal("a2\n", GitSandbox.Read(project, "a.txt"));
        Assert.Null(await box.Updates.LoadStateAsync(project));
    }

    [GitFact]
    public async Task Switching_your_own_branch_brings_uncommitted_edits_along()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", "one\ntwo\nthree\n"), ("b.txt", "b\n"));
        var project = box.CloneProject();
        GitSandbox.Run(project, "checkout", "-q", "-b", "feature");
        GitSandbox.Commit(project, "feature work", ("a.txt", "one\ntwo\nthree FEATURE\n"));
        GitSandbox.Run(project, "checkout", "-q", "developer");
        GitSandbox.Write(project, "a.txt", "one MINE\ntwo\nthree\n");
        GitSandbox.Write(project, "b.txt", "b MINE\n");

        var branches = await box.Updates.ListProjectBranchesAsync(project);
        Assert.Equal(new[] { "developer", "feature" }, branches.Select(b => b.Display));
        Assert.True(branches[0].IsCurrent);
        var plan = await box.Updates.PlanBranchSwitchAsync(project, branches[1]);
        Assert.True(plan.CanSwitch);
        Assert.Equal(new[] { "a.txt" }, plan.CollidingPaths);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.SwitchBranchAsync(project, plan)).Kind);
        Assert.Equal("feature", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("one MINE\ntwo\nthree FEATURE\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal("b MINE\n", GitSandbox.Read(project, "b.txt"));
        Assert.Equal(" M a.txt\n M b.txt", GitSandbox.Status(project));
        Assert.Null(await box.Updates.LoadStateAsync(project));
    }

    [GitFact]
    public async Task Overlapping_edit_on_a_branch_switch_can_be_kept_or_undone()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", "one\ntwo\nthree\n"));
        var project = box.CloneProject();
        GitSandbox.Run(project, "checkout", "-q", "-b", "feature");
        GitSandbox.Commit(project, "feature work", ("a.txt", "one\ntwo\nthree FEATURE\n"));
        GitSandbox.Run(project, "checkout", "-q", "developer");
        GitSandbox.Write(project, "a.txt", "one\ntwo\nthree MINE\n");
        var feature = new ProjectBranch("feature", null, false);

        var result = await box.Updates.SwitchBranchAsync(project, await box.Updates.PlanBranchSwitchAsync(project, feature));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdatePhase.RestoringChanges, result.Phase);
        Assert.Equal(BasisOperation.BranchSwitch, (await box.Updates.LoadStateAsync(project))?.Operation);
        Assert.Equal("feature", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));

        Assert.Equal(BasisUpdateResultKind.Aborted, (await box.Updates.AbortAsync(project)).Kind);
        Assert.Equal("developer", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("one\ntwo\nthree MINE\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal(" M a.txt", GitSandbox.Status(project));
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));

        Assert.Equal(BasisUpdateResultKind.Conflicts, (await box.Updates.SwitchBranchAsync(project, await box.Updates.PlanBranchSwitchAsync(project, feature))).Kind);
        Assert.True((await box.Updates.ResolveAsync(project, "a.txt", ConflictChoice.Mine)).Ok);
        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.ContinueAsync(project)).Kind);
        Assert.Equal("feature", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("one\ntwo\nthree MINE\n", GitSandbox.Read(project, "a.txt"));
        Assert.Equal(" M a.txt", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Branch_switch_refuses_to_overwrite_ignored_files()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", "a\n"));
        var project = box.CloneProject();
        GitSandbox.Run(project, "checkout", "-q", "-b", "feature");
        GitSandbox.Commit(project, "embed package", ("Packages/com.example/package.json", "{ \"basis\": true }\n"));
        GitSandbox.Run(project, "checkout", "-q", "developer");
        File.AppendAllText(Path.Combine(project, ".git", "info", "exclude"), "\nPackages/com.example/\n");
        GitSandbox.Write(project, "Packages/com.example/package.json", "{ \"mounted\": true }\n");

        var plan = await box.Updates.PlanBranchSwitchAsync(project, new ProjectBranch("feature", null, false));
        Assert.False(plan.CanSwitch);
        Assert.Equal(new[] { "Packages/com.example/package.json" }, plan.BlockingPaths);
        Assert.Equal(BasisUpdateFailure.IgnoredFilesInTheWay, (await box.Updates.SwitchBranchAsync(project, plan)).Failure);
        Assert.Equal("developer", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("{ \"mounted\": true }\n", GitSandbox.Read(project, "Packages/com.example/package.json"));
    }

    [GitFact]
    public async Task Branch_only_on_your_remote_is_checked_out_and_tracked()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", ("a.txt", "a\n"));
        var project = box.CloneProject();
        var studio = box.CreateBareRepository("studio.git");
        GitSandbox.Run(project, "remote", "set-url", "origin", studio);
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "developer");
        GitSandbox.Run(project, "checkout", "-q", "-b", "team-feature");
        GitSandbox.Commit(project, "team work", ("team.txt", "team\n"));
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "team-feature");
        GitSandbox.Run(project, "checkout", "-q", "developer");
        GitSandbox.Run(project, "branch", "-q", "-D", "team-feature");

        var remoteOnly = Assert.Single(await box.Updates.ListProjectBranchesAsync(project), b => b.Remote is not null);
        Assert.Equal("origin/team-feature", remoteOnly.Display);
        var plan = await box.Updates.PlanBranchSwitchAsync(project, remoteOnly);
        Assert.True(plan.CreatesBranch);

        Assert.Equal(BasisUpdateResultKind.Updated, (await box.Updates.SwitchBranchAsync(project, plan)).Kind);
        Assert.Equal("team-feature", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("origin/team-feature", GitSandbox.Run(project, "rev-parse", "--abbrev-ref", "@{u}"));
        Assert.Equal("team\n", GitSandbox.Read(project, "team.txt"));
    }

    [Fact]
    public void Basis_record_is_read_from_commit_trailers()
    {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        var record = BasisUpdateService.ParseRecord("c0ffee", $"Update Basis to developer 012345678\n\nBasis-Branch: {Lts}\nBasis-Commit: {sha}\nBasis-Folder: Basis -> Game/Unity\n");

        Assert.NotNull(record);
        Assert.Equal(sha, record!.BasisSha);
        Assert.Equal(Lts, record.Branch);
        Assert.Equal(new BasisLayout("Basis", "Game/Unity"), record.Layout);
        Assert.Null(BasisUpdateService.ParseRecord("c0ffee", "Basis-Commit: not-a-sha\n"));
        Assert.Equal(BasisLayout.Whole, BasisUpdateService.ParseRecord("c0ffee", $"Basis-Commit: {sha}\nBasis-Folder: ../escape -> .\n")!.Layout);
    }

    [Theory]
    [InlineData(". -> .", "", "")]
    [InlineData("Basis -> .", "Basis", "")]
    [InlineData("Basis -> Game/Unity", "Basis", "Game/Unity")]
    public void Layouts_round_trip(string text, string basisFolder, string projectFolder)
    {
        var layout = BasisLayout.Parse(text);
        Assert.Equal(new BasisLayout(basisFolder, projectFolder), layout);
        Assert.Equal(text, layout!.ToString());
    }

    [GitFact]
    public async Task Newest_basis_ref_wins_over_one_left_by_an_older_fetch()
    {
        using var box = new GitSandbox();
        var old = box.CommitUpstream("base", ("a.txt", "a\n"));
        var project = box.CloneProject();
        GitSandbox.Run(project, "update-ref", BasisUpdateService.UpstreamRefPrefix + "developer", old);
        var pulled = box.CommitUpstream("pulled later", ("a.txt", "b\n"));
        GitSandbox.Run(project, "pull", "-q", "--ff-only");

        Assert.Equal(pulled, (await box.Updates.ResolveBasisBaseAsync(project))!.Sha);

        var fetched = box.CommitUpstream("fetched by BasisPM", ("a.txt", "c\n"));
        GitSandbox.Run(project, "fetch", "-q", box.Upstream, "developer:" + BasisUpdateService.UpstreamRefPrefix + "developer");
        GitSandbox.Run(project, "merge", "-q", "--ff-only", fetched);

        Assert.Equal(fetched, (await box.Updates.ResolveBasisBaseAsync(project))!.Sha);
    }
}
