using BasisPM.Core.Models;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class SetAsideTests
{
    // Enough long paths that one command line holding them all passes Windows' 32,767-character limit.
    private static readonly string[] Files = Enumerable.Range(0, 700)
        .Select(i => $"Basis/Packages/com.basis.example/Runtime/Deeply/Nested/Folder/Asset_{i:D4}_with_a_long_descriptive_name.txt").ToArray();

    [GitFact]
    public async Task Update_sets_aside_hundreds_of_edited_files_and_puts_them_back()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", Files.Select(f => (f, (string?)"one\ntwo\nthree\n")).ToArray());
        var project = box.CloneProject();
        foreach (var file in Files) GitSandbox.Write(project, file, "one\ntwo\nmine\n");
        StageAvatarWork(project);
        var tip = box.CommitUpstream("basis edits", Files.Select(f => (f, (string?)"ONE\ntwo\nthree\n")).ToArray());

        var plan = await box.Updates.PlanAsync(project);
        Assert.Equal(Files.Length, plan.CollidingPaths.Count);
        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.True(result.Kind == BasisUpdateResultKind.Updated, $"{result.Kind} {result.Failure}: {result.Detail}");
        Assert.Equal(tip, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.All(Files, f => Assert.Equal("ONE\ntwo\nmine\n", GitSandbox.Read(project, f)));
        AssertAvatarWorkStaged(project);
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
    }

    [GitFact]
    public async Task Abort_after_setting_aside_hundreds_of_files_leaves_every_edit_in_place()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("base", Files.Select(f => (f, (string?)"one\ntwo\nthree\n")).ToArray());
        var project = box.CloneProject();
        foreach (var file in Files) GitSandbox.Write(project, file, "one\ntwo\nmine\n");
        GitSandbox.Write(project, Files[0], "MINE\ntwo\nthree\n");
        StageAvatarWork(project);
        var head = GitSandbox.Run(project, "rev-parse", "HEAD");
        box.CommitUpstream("basis edits", Files.Select(f => (f, (string?)"ONE\ntwo\nthree\n")).ToArray());

        var result = await box.Updates.ApplyAsync(project, await box.Updates.PlanAsync(project));
        Assert.Equal(BasisUpdateResultKind.Conflicts, result.Kind);
        Assert.Equal(BasisUpdateResultKind.Aborted, (await box.Updates.AbortAsync(project)).Kind);

        Assert.Equal(head, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal("MINE\ntwo\nthree\n", GitSandbox.Read(project, Files[0]));
        Assert.All(Files.Skip(1), f => Assert.Equal("one\ntwo\nmine\n", GitSandbox.Read(project, f)));
        AssertAvatarWorkStaged(project);
        Assert.Equal("", GitSandbox.Run(project, "stash", "list"));
    }

    // Staged work the update doesn't touch: a new file, a staged file edited again since, and a staged file since deleted
    // from disk, whose only copy is the index.
    private static void StageAvatarWork(string project)
    {
        GitSandbox.Write(project, "Assets/Avatar/new.prefab", "staged\n");
        GitSandbox.Write(project, "Assets/Avatar/body.mat", "staged\n");
        GitSandbox.Write(project, "Assets/Avatar/gone.mat", "only in the index\n");
        GitSandbox.Run(project, "add", "Assets/Avatar");
        GitSandbox.Write(project, "Assets/Avatar/body.mat", "edited after staging\n");
        GitSandbox.Write(project, "Assets/Avatar/gone.mat", null);
    }

    private static void AssertAvatarWorkStaged(string project)
    {
        Assert.Equal("AM Assets/Avatar/body.mat\nAD Assets/Avatar/gone.mat\nA  Assets/Avatar/new.prefab",
            GitSandbox.Run(project, "status", "--porcelain=v1", "--", "Assets/Avatar").Replace("\r", ""));
        Assert.Equal("staged", GitSandbox.Run(project, "show", ":Assets/Avatar/body.mat"));
        Assert.Equal("only in the index", GitSandbox.Run(project, "show", ":Assets/Avatar/gone.mat"));
        Assert.Equal("edited after staging\n", GitSandbox.Read(project, "Assets/Avatar/body.mat"));
    }
}
