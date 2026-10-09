using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class LocalWorkTests
{
    private static (GitSandbox Box, MountService Mounts, string Clone) Setup()
    {
        var box = new GitSandbox();
        box.CommitUpstream("v1", ("a.txt", "a\n"));
        GitSandbox.Run(box.Upstream, "tag", "v1");
        box.CommitUpstream("v2", ("a.txt", "b\n"));
        return (box, new MountService(box.Git, new UnityProjectService(), new MountRegistry()), box.CloneProject());
    }

    [GitFact]
    public async Task A_fresh_clone_on_its_branch_or_at_a_tag_has_no_local_work()
    {
        var (box, mounts, clone) = Setup();
        using var _ = box;

        Assert.False(await mounts.HasLocalWorkAsync(clone));
        GitSandbox.Run(clone, "checkout", "-q", "v1");
        Assert.False(await mounts.HasLocalWorkAsync(clone));
    }

    [GitFact]
    public async Task Commits_made_on_a_detached_head_are_local_work()
    {
        var (box, mounts, clone) = Setup();
        using var _ = box;
        GitSandbox.Run(clone, "checkout", "-q", "v1");

        GitSandbox.Commit(clone, "my fix", ("a.txt", "fixed\n"));

        Assert.True(await mounts.HasLocalWorkAsync(clone));
    }

    [GitFact]
    public async Task A_stash_is_local_work_even_with_a_clean_tree()
    {
        var (box, mounts, clone) = Setup();
        using var _ = box;
        GitSandbox.Write(clone, "a.txt", "edit\n");
        GitSandbox.Run(clone, "stash", "-q");

        Assert.Equal("", GitSandbox.Status(clone));
        Assert.True(await mounts.HasLocalWorkAsync(clone));
    }

    [GitFact]
    public async Task Unpushed_commits_on_another_local_branch_are_local_work()
    {
        var (box, mounts, clone) = Setup();
        using var _ = box;
        GitSandbox.Run(clone, "checkout", "-q", "-b", "feature");
        GitSandbox.Commit(clone, "feature work", ("b.txt", "b\n"));
        GitSandbox.Run(clone, "checkout", "-q", "v1");

        Assert.True(await mounts.HasLocalWorkAsync(clone));
    }

    [GitFact]
    public async Task Ahead_of_its_upstream_and_uncommitted_edits_are_local_work()
    {
        var (box, mounts, clone) = Setup();
        using var _ = box;
        GitSandbox.Write(clone, "a.txt", "edit\n");
        Assert.True(await mounts.HasLocalWorkAsync(clone));

        GitSandbox.Run(clone, "checkout", "-q", "--", "a.txt");
        GitSandbox.Commit(clone, "ahead", ("c.txt", "c\n"));
        Assert.True(await mounts.HasLocalWorkAsync(clone));
    }
}
