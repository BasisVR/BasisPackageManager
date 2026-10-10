using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisPartsServiceTests
{
    private static readonly BasisPart[] Both = { BasisPartsService.Server, BasisPartsService.Images };

    private static string Unity(string project) => Path.Combine(project, "Basis");

    private static bool Exists(string project, string path) => File.Exists(Path.Combine(project, path)) || Directory.Exists(Path.Combine(project, path));

    private static void CommitBasis(GitSandbox box) => box.CommitUpstream("basis",
        (".gitattributes", "* -text\n"),
        ("README.md", "readme\n"),
        ("Basis Server/.gitignore", "bin/\n"),
        ("Basis Server/BasisServerConsole/Program.cs", "server\n"),
        ("Basis Server/Packages/.gitignore", "/*/\n"),
        ("Basis Server/Packages/manifest.json", "{}\n"),
        ("Basis/Images/Banner.png", "banner\n"),
        ("Basis/Assets/Scene.unity", "scene\n"),
        ("Basis/Packages/com.basis.framework/package.json", "{}\n"),
        ("Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.0f1\n"));

    private static async Task<string> CloneWithoutAsync(GitSandbox box, params BasisPart[] parts)
    {
        var project = box.Combine("project");
        var result = await box.Git.CloneAsync(box.Upstream, project, "developer", null, default, BasisPartsService.ClonePatterns(parts));
        Assert.True(result.Ok, result.Output);
        GitSandbox.Run(project, "config", "core.autocrlf", "false");
        return project;
    }

    [Fact]
    public void Clone_patterns_take_everything_but_the_parts_left_out()
    {
        Assert.Equal(new[] { "/*", "!/Basis Server/", "!/Basis/Images/" }, BasisPartsService.ClonePatterns(Both));
        Assert.Empty(BasisPartsService.ClonePatterns(Array.Empty<BasisPart>()));
        Assert.Equal(new[] { "/*", "!/My \\[Game\\]/Images/" }, BasisPartsService.Patterns(new[] { "My [Game]/Images" }));
    }

    [Fact]
    public void Parts_are_found_by_id_or_name()
    {
        Assert.Same(BasisPartsService.Server, BasisPartsService.Find("server"));
        Assert.Same(BasisPartsService.Server, BasisPartsService.Find(" Basis Server "));
        Assert.Same(BasisPartsService.Images, BasisPartsService.Find("IMAGES"));
        Assert.Null(BasisPartsService.Find("assets"));
        Assert.Equal(Both, BasisPartsService.FromIds(new[] { "server", "images", "server", "nope" }));
    }

    [GitFact]
    public async Task Clone_leaves_out_the_chosen_parts_and_still_takes_new_basis_folders()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = await CloneWithoutAsync(box, Both);

        Assert.False(Exists(project, "Basis Server"));
        Assert.False(Exists(project, "Basis/Images"));
        Assert.True(Exists(project, "Basis/Assets/Scene.unity"));
        Assert.True(Exists(project, "README.md"));
        Assert.Equal("", GitSandbox.Status(project));
        var report = await new BasisPartsService(box.Git).ScanAsync(project, Unity(project));
        Assert.Equal(BasisPartsMode.Managed, report.Mode);
        Assert.Equal(Both, report.LeftOut);

        box.CommitUpstream("more", ("docs/guide.md", "guide\n"), ("Basis/Tools/tool.txt", "tool\n"), ("Basis Server/BasisServerConsole/Program.cs", "server 2\n"), ("Basis/Images/Logo.png", "logo\n"));
        GitSandbox.Run(project, "pull", "-q", "--ff-only");

        Assert.True(Exists(project, "docs/guide.md"));
        Assert.True(Exists(project, "Basis/Tools/tool.txt"));
        Assert.False(Exists(project, "Basis Server"));
        Assert.False(Exists(project, "Basis/Images"));
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Leaving_the_server_out_deletes_its_build_output_but_keeps_package_clones()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        GitSandbox.Write(project, "Basis Server/BasisServerConsole/bin/Release/server.dll", "built\n");
        var clone = Path.Combine(project, "Basis Server", "Packages", "com.example.server");
        GitSandbox.Run(project, "init", "-q", clone);
        GitSandbox.Write(clone, "Plugin.cs", "plugin\n");
        var parts = new BasisPartsService(box.Git);

        var before = await parts.ScanAsync(project, Unity(project));
        var server = Assert.Single(before.Parts, p => p.Part == BasisPartsService.Server);
        Assert.Equal(BasisPartsMode.Complete, before.Mode);
        Assert.True(server.Included);
        Assert.False(server.HasUnsavedWork);
        Assert.Equal(1, server.IgnoredFiles);
        Assert.Equal(new[] { "Basis Server/Packages/com.example.server" }, server.Repositories);

        var result = await parts.ApplyAsync(project, Unity(project), new[] { BasisPartsService.Server }, deleteIgnored: true);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(new[] { BasisPartsService.Server }, result.Removed);
        Assert.Equal(1, result.DeletedFiles);
        Assert.Equal(new[] { "Basis Server/Packages/com.example.server" }, result.Kept);
        Assert.False(Exists(project, "Basis Server/BasisServerConsole"));
        Assert.False(Exists(project, "Basis Server/Packages/manifest.json"));
        Assert.True(Exists(project, "Basis Server/Packages/com.example.server/Plugin.cs"));
        Assert.True(Exists(project, "Basis/Images/Banner.png"));
        Assert.Equal("", GitSandbox.Status(project));
        Assert.Equal(new[] { BasisPartsService.Server }, (await parts.ScanAsync(project, Unity(project), inspect: false)).LeftOut);
    }

    [GitFact]
    public async Task Ignored_files_can_be_kept_when_a_part_is_left_out()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        GitSandbox.Write(project, "Basis Server/BasisServerConsole/bin/server.dll", "built\n");

        var result = await new BasisPartsService(box.Git).ApplyAsync(project, Unity(project), new[] { BasisPartsService.Server }, deleteIgnored: false);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(1, result.IgnoredFilesLeft);
        Assert.True(Exists(project, "Basis Server/BasisServerConsole/bin/server.dll"));
        Assert.False(Exists(project, "Basis Server/BasisServerConsole/Program.cs"));
    }

    [GitFact]
    public async Task A_part_with_unsaved_work_is_not_left_out()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        GitSandbox.Write(project, "Basis Server/BasisServerConsole/Program.cs", "my edit\n");
        GitSandbox.Write(project, "Basis/Images/Mine.png", "mine\n");
        var parts = new BasisPartsService(box.Git);

        var report = await parts.ScanAsync(project, Unity(project));
        Assert.Equal(new[] { "Basis Server/BasisServerConsole/Program.cs" }, report.Parts.Single(p => p.Part == BasisPartsService.Server).Unsaved);
        Assert.Equal(new[] { "Basis/Images/Mine.png" }, report.Parts.Single(p => p.Part == BasisPartsService.Images).Unsaved);

        var result = await parts.ApplyAsync(project, Unity(project), Both, deleteIgnored: true);

        Assert.Equal(BasisPartsFailure.UnsavedWork, result.Failure);
        Assert.Contains("Basis Server/BasisServerConsole/Program.cs", result.Detail);
        Assert.Equal("my edit\n", GitSandbox.Read(project, "Basis Server/BasisServerConsole/Program.cs"));
        Assert.Equal(BasisPartsMode.Complete, (await parts.ScanAsync(project, Unity(project), inspect: false)).Mode);
    }

    [GitFact]
    public async Task A_part_deleted_by_hand_can_still_be_left_out()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        Directory.Delete(Path.Combine(project, "Basis", "Images"), recursive: true);

        var result = await new BasisPartsService(box.Git).ApplyAsync(project, Unity(project), new[] { BasisPartsService.Images }, deleteIgnored: true);

        Assert.True(result.Ok, result.Detail);
        Assert.Equal("", GitSandbox.Status(project));
    }

    [GitFact]
    public async Task Adding_every_part_back_restores_the_files_and_turns_sparse_checkout_off()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = await CloneWithoutAsync(box, Both);
        var parts = new BasisPartsService(box.Git);

        var serverBack = await parts.ApplyAsync(project, Unity(project), new[] { BasisPartsService.Images }, deleteIgnored: true);
        Assert.True(serverBack.Ok, serverBack.Detail);
        Assert.Equal(new[] { BasisPartsService.Server }, serverBack.Added);
        Assert.Equal("server\n", GitSandbox.Read(project, "Basis Server/BasisServerConsole/Program.cs"));
        Assert.False(Exists(project, "Basis/Images"));

        var allBack = await parts.ApplyAsync(project, Unity(project), Array.Empty<BasisPart>(), deleteIgnored: true);
        Assert.True(allBack.Ok, allBack.Detail);
        Assert.Equal(new[] { BasisPartsService.Images }, allBack.Added);
        Assert.Equal("banner\n", GitSandbox.Read(project, "Basis/Images/Banner.png"));
        Assert.False(await box.Git.GetConfigFlagAsync(project, "core.sparseCheckout"));
        Assert.Equal(BasisPartsMode.Complete, (await parts.ScanAsync(project, Unity(project))).Mode);
        Assert.Equal("", GitSandbox.Status(project));

        Assert.False((await parts.ApplyAsync(project, Unity(project), Array.Empty<BasisPart>(), deleteIgnored: true)).Changed);
    }

    [GitFact]
    public async Task A_sparse_checkout_set_up_by_hand_is_left_alone()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        GitSandbox.Run(project, "sparse-checkout", "set", "Basis");
        var parts = new BasisPartsService(box.Git);

        Assert.Equal(BasisPartsMode.Custom, (await parts.ScanAsync(project, Unity(project))).Mode);
        var result = await parts.ApplyAsync(project, Unity(project), new[] { BasisPartsService.Images }, deleteIgnored: true);

        Assert.Equal(BasisPartsFailure.CustomSparseCheckout, result.Failure);
        Assert.Equal("Basis", GitSandbox.Run(project, "sparse-checkout", "list"));
    }

    [GitFact]
    public async Task Parts_are_not_changed_while_a_merge_is_unfinished()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = box.CloneProject();
        File.WriteAllText(Path.Combine(project, ".git", "MERGE_HEAD"), GitSandbox.Run(project, "rev-parse", "HEAD") + "\n");

        var result = await new BasisPartsService(box.Git).ApplyAsync(project, Unity(project), new[] { BasisPartsService.Server }, deleteIgnored: true);

        Assert.Equal(BasisPartsFailure.OperationInProgress, result.Failure);
        Assert.True(Exists(project, "Basis Server/BasisServerConsole/Program.cs"));
    }

    [GitFact]
    public async Task Parts_missing_from_the_project_are_not_offered()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("basis", ("Basis/Assets/Scene.unity", "scene\n"), ("Basis/Packages/com.basis.framework/package.json", "{}\n"));
        var project = box.CloneProject();

        Assert.Empty((await new BasisPartsService(box.Git).ScanAsync(project, Unity(project))).Parts);
    }

    [GitFact]
    public async Task Basis_updates_keep_left_out_parts_out()
    {
        using var box = new GitSandbox();
        CommitBasis(box);
        var project = await CloneWithoutAsync(box, BasisPartsService.Server);
        GitSandbox.Commit(project, "my game", ("Basis/Assets/Game.txt", "game\n"));
        box.CommitUpstream("basis work", ("Basis Server/BasisServerConsole/Program.cs", "server 2\n"), ("Basis/Assets/Scene.unity", "scene 2\n"), ("Basis Server/NewTool/Tool.cs", "tool\n"));
        await box.Updates.GetBasisHeadsAsync(refresh: true);

        var plan = await box.Updates.PlanAsync(project);
        var result = await box.Updates.ApplyAsync(project, plan);

        Assert.Equal(BasisUpdateResultKind.Updated, result.Kind);
        Assert.Equal("scene 2\n", GitSandbox.Read(project, "Basis/Assets/Scene.unity"));
        Assert.Equal("game\n", GitSandbox.Read(project, "Basis/Assets/Game.txt"));
        Assert.False(Exists(project, "Basis Server"));
        Assert.Equal("server 2", GitSandbox.Run(project, "show", "HEAD:Basis Server/BasisServerConsole/Program.cs"));
        Assert.Equal("", GitSandbox.Status(project));
        Assert.Equal(new[] { BasisPartsService.Server }, (await new BasisPartsService(box.Git).ScanAsync(project, Unity(project))).LeftOut);
    }
}
