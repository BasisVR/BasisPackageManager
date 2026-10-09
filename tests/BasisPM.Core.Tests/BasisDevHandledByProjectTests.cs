using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed partial class BasisDevServiceTests
{
    [GitFact]
    public async Task Clone_of_a_package_the_project_tracks_can_be_left_to_the_project()
    {
        using var p = new Project("{}", ("Packages/com.x/package.json", """{ "name": "com.x" }"""));
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");
        Assert.Contains(BasisDevAction.Ignore, (await p.Service.ScanAsync(p.Install)).Find("com.x")!.Actions);

        var ignored = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Ignore);

        Assert.True(ignored.Ok, ignored.Message);
        var package = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Empty(package.Issues);
        Assert.True(package.HandledByProject);
        Assert.Equal(PackageSourceKind.ProjectRepo, package.Source);
        Assert.Equal(new[] { BasisDevAction.Unignore, BasisDevAction.Release }, package.Actions);
        Assert.NotNull(package.Comparison);
        Assert.True(BasisDevStore.Read(p.Unity, "com.x")!.HandledByProject);
        Assert.Contains("\"handledByProject\": true", File.ReadAllText(BasisDevStore.SidecarPath(p.Unity, "com.x")));
        Assert.Empty(p.Service.Scan(p.Install).WithIssues);
        Assert.True(Directory.Exists(clone));

        Assert.True((await p.Service.ReconcileAsync(p.Install)).Ok);
        Assert.True(BasisDevStore.Read(p.Unity, "com.x")!.HandledByProject);

        var flagged = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Unignore);
        Assert.True(flagged.Ok, flagged.Message);
        Assert.Contains(BasisDevIssueKind.Shadowed, (await p.Service.ScanAsync(p.Install)).Find("com.x")!.Issues);
        Assert.DoesNotContain("handledByProject", File.ReadAllText(BasisDevStore.SidecarPath(p.Unity, "com.x")));
    }

    [GitFact]
    public async Task Clone_the_manifest_does_not_point_at_can_be_left_to_the_manifest_and_used_later()
    {
        const string pinned = "https://github.com/example/upstream.git?path=Packages/com.x#abc1234";
        using var p = new Project("{ \"com.x\": \"" + pinned + "\" }");
        var clone = p.MakeClone(".basisdev/com.x", "com.x", "Packages/com.x");

        Assert.True((await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Ignore)).Ok);
        var ignored = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.Empty(ignored.Issues);
        Assert.Equal(PackageSourceKind.Git, ignored.Source);
        Assert.NotNull(ignored.Sidecar?.Upstream.Url);
        Assert.Equal(pinned, p.Manifest()["com.x"]);

        Assert.False((await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.UseClone)).Ok);
        Assert.True((await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Unignore)).Ok);
        var used = await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.UseClone, force: true);
        Assert.True(used.Ok, used.Message);
        var active = (await p.Service.ScanAsync(p.Install)).Find("com.x")!;
        Assert.True(active.CloneActive);
        Assert.Null(BasisDevStore.Read(p.Unity, "com.x")!.HandledByProject);
        Assert.False((await p.Service.ApplyAsync(p.Install, "com.x", BasisDevAction.Ignore)).Ok);
        Assert.True(Directory.Exists(clone));
    }
}
