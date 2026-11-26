using System.Net;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisCompareTargetTests
{
    private static string SeedBasis(GitSandbox box) => box.CommitUpstream("basis",
        ("README.md", "basis\n"),
        ("Basis/Assets/Scene.unity", "scene\n"),
        ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.7.0b10\nm_EditorVersionWithRevision: 6000.7.0b10 (abc)\n"),
        ("Basis/Packages/manifest.json", "{}\n"),
        ("Basis/Packages/com.basis.a/package.json", "{\"name\":\"com.basis.a\"}\n"),
        ("Basis/Packages/com.basis.a/Runtime/A.cs", "class A {}\n"),
        ("Basis/Packages/com.basis.b/package.json", "{\"name\":\"com.basis.b\"}\n"));

    private static BasisContributeService Service(GitSandbox box) =>
        new(box.Git, box.Updates, new GitHubApiService(StubHttpMessageHandler.AlwaysStatus(HttpStatusCode.NotFound)), "BasisVR/Basis",
            (_, _, _) => throw new InvalidOperationException("no push target"));

    [GitFact]
    public async Task Latest_compares_with_the_newest_basis_commit_and_cannot_be_sent()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/A.cs", "class A { int x; }\n");
        var tip = box.CommitUpstream("basis moved on", ("Basis/Packages/com.basis.b/package.json", "{\"name\":\"com.basis.b\",\"version\":\"2.0.0\"}\n"));
        var service = Service(box);

        var based = await service.ScanAsync(project, Path.Combine(project, "Basis"));
        var latest = await service.ScanAsync(project, Path.Combine(project, "Basis"), target: BasisCompareTarget.Latest);

        Assert.True(based.CanContribute);
        Assert.Equal(new[] { "Basis/Packages/com.basis.a/Runtime/A.cs" }, based.Changes.Select(c => c.Path));
        Assert.Equal(BasisContributeBlock.None, latest.Block);
        Assert.Equal(BasisCompareTarget.Latest, latest.Target);
        Assert.Equal(tip, latest.CompareSha);
        Assert.Equal("developer", latest.CompareLabel);
        Assert.Equal("basis moved on", latest.CompareCommit?.Subject);
        Assert.Equal(new[] { "Basis/Packages/com.basis.a/Runtime/A.cs", "Basis/Packages/com.basis.b/package.json" }, latest.Changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.False(latest.CanContribute);
        Assert.False((await service.CreateCommitAsync(latest, latest.Changes.Select(c => c.Path), "x", "a", "a@b")).Ok);
        Assert.Contains("+{\"name\":\"com.basis.b\"}", (await service.GetDiffAsync(latest, latest.Changes.Single(c => c.Path.EndsWith("com.basis.b/package.json")))).Select(l => l.Text));
    }

    [GitFact]
    public async Task Upstream_compares_with_the_tracked_branch_on_the_projects_own_remote()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        var team = box.CreateBareRepository("team.git");
        GitSandbox.Run(project, "remote", "rename", "origin", "basis");
        GitSandbox.Run(project, "remote", "add", "origin", team);
        GitSandbox.Commit(project, "pushed work", ("Basis/Assets/Scene.unity", "team scene\n"));
        GitSandbox.Run(project, "push", "-q", "-u", "origin", "developer");
        GitSandbox.Commit(project, "local work", ("notes.txt", "mine\n"));
        GitSandbox.Write(project, "Basis/Packages/com.basis.a/Runtime/A.cs", "class A { int y; }\n");

        var scan = await Service(box).ScanAsync(project, Path.Combine(project, "Basis"), target: BasisCompareTarget.ProjectUpstream);

        Assert.Equal(BasisContributeBlock.None, scan.Block);
        Assert.Equal("origin/developer", scan.CompareLabel);
        Assert.Equal(new[] { "Basis/Packages/com.basis.a/Runtime/A.cs", "notes.txt" }, scan.Changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal));
        Assert.False(scan.CanContribute);
    }

    [GitFact]
    public async Task Upstream_is_blocked_when_the_branch_tracks_nothing()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        GitSandbox.Run(project, "checkout", "-q", "-b", "solo");

        var scan = await Service(box).ScanAsync(project, Path.Combine(project, "Basis"), target: BasisCompareTarget.ProjectUpstream);

        Assert.Equal(BasisContributeBlock.NoUpstream, scan.Block);
        Assert.Equal("solo", scan.Detail);
    }

    [GitFact]
    public async Task Incoming_unity_version_reads_the_update_and_the_branch_target()
    {
        using var box = new GitSandbox();
        SeedBasis(box);
        var project = box.CloneProject();
        box.CommitUpstream("unity bump", ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.7.0b11\nm_EditorVersionWithRevision: 6000.7.0b11 (def)\n"));

        var plan = await box.Updates.PlanAsync(project);
        Assert.True(plan.CanApply);
        Assert.Equal("6000.7.0b11", await box.Updates.IncomingUnityVersionAsync(project, plan));

        GitSandbox.Run(project, "fetch", "-q", "origin");
        var switchPlan = await box.Updates.PlanBranchSwitchAsync(project, new ProjectBranch("developer", "origin", false));
        Assert.Equal("6000.7.0b10", await box.Updates.IncomingUnityVersionAsync(project, Path.Combine(project, "Basis"), switchPlan));
        GitSandbox.Run(project, "checkout", "-q", "-b", "bumped", "origin/developer");
        GitSandbox.Run(project, "checkout", "-q", "developer");
        var bumped = await box.Updates.PlanBranchSwitchAsync(project, new ProjectBranch("bumped", null, false));
        Assert.Equal("6000.7.0b11", await box.Updates.IncomingUnityVersionAsync(project, Path.Combine(project, "Basis"), bumped));
    }

    [Fact]
    public void Editor_version_parses_from_project_version_text()
    {
        Assert.Equal("6000.7.0b11", UnityProjectService.ParseEditorVersion("m_EditorVersion: 6000.7.0b11\r\nm_EditorVersionWithRevision: 6000.7.0b11 (def)\r\n"));
        Assert.Null(UnityProjectService.ParseEditorVersion("m_EditorVersionWithRevision: 6000.7.0b11 (def)\n"));
        Assert.Null(UnityProjectService.ParseEditorVersion(null));
        Assert.Null(UnityProjectService.ParseEditorVersion("m_EditorVersion:   \n"));
    }
}
