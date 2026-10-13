using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisChannelDetectionTests
{
    private const string Experimental = "experimental-branch-major-changes";

    [GitFact]
    public async Task Rehosted_experimental_project_keeps_following_experimental()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"), ("b.txt", "b1\n"));
        GitSandbox.Run(box.Upstream, "checkout", "-q", "-b", Experimental);
        box.CommitUpstream("experimental api", ("api.txt", "new\n"));
        GitSandbox.Run(box.Upstream, "checkout", "-q", "developer");
        box.CommitUpstream("two", ("a.txt", "a2\n"));
        var project = box.CloneProject("project", Experimental);
        var studio = box.CreateBareRepository("studio.git");
        GitSandbox.Run(project, "branch", "-m", "main");
        GitSandbox.Run(project, "remote", "set-url", "origin", studio);
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "main");
        GitSandbox.Commit(project, "my world", ("world.txt", "world\n"));

        var plan = await box.Updates.PlanAsync(project);

        Assert.Equal(Experimental, plan.BasisBranch);
        Assert.Equal(BasisUpdateKind.UpToDate, plan.Kind);
        Assert.Equal(BasisChannel.Experimental, BasisChannels.Classify((await box.Updates.ResolveBasisBaseAsync(project))!.Branch));
    }

    [GitFact]
    public async Task A_project_from_before_experimental_split_off_still_follows_developer()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("one", ("a.txt", "a1\n"));
        var project = box.CloneProject("project");
        GitSandbox.Run(box.Upstream, "checkout", "-q", "-b", Experimental);
        box.CommitUpstream("experimental api", ("api.txt", "new\n"));
        GitSandbox.Run(box.Upstream, "checkout", "-q", "developer");
        var studio = box.CreateBareRepository("studio.git");
        GitSandbox.Run(project, "branch", "-m", "main");
        GitSandbox.Run(project, "remote", "set-url", "origin", studio);
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "main");

        var plan = await box.Updates.PlanAsync(project);

        Assert.Equal("developer", plan.BasisBranch);
    }
}
