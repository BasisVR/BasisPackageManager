using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ProjectSyncConflictTests
{
    [GitFact]
    public async Task Sync_reports_unmerged_files_as_conflicts_and_does_not_pull()
    {
        using var t = new TempDir("sync-conflict");
        var remote = t.CreateDir("remote");
        GitSandbox.Run(remote, "init", "-q", "-b", "developer");
        GitSandbox.Run(remote, "config", "core.autocrlf", "false");
        GitSandbox.Commit(remote, "base", ("Proj/ProjectSettings/ProjectSettings.asset", "a\n"), ("Proj/Packages/manifest.json", """{ "dependencies": {} }"""));
        var project = t.Combine("clone");
        GitSandbox.Run(t.Combine(), "clone", "-q", remote, project);
        GitSandbox.Run(project, "config", "core.autocrlf", "false");
        GitSandbox.Commit(remote, "theirs", ("Proj/ProjectSettings/ProjectSettings.asset", "theirs\n"));
        File.WriteAllText(Path.Combine(project, "Proj", "ProjectSettings", "ProjectSettings.asset"), "mine\n");
        GitSandbox.Run(project, "stash", "-q");
        GitSandbox.Run(project, "pull", "-q", "--ff-only");
        try { GitSandbox.Run(project, "stash", "pop", "-q"); } catch (Exception) { }
        var head = GitSandbox.Run(project, "rev-parse", "HEAD");
        GitSandbox.Commit(remote, "later", ("Proj/readme.txt", "later\n"));

        var git = new GitService(allowLocalRemotes: true);
        var registry = new MountRegistry(t.CreateDir("data"));
        var sync = new ProjectSyncService(git, new BasisDevService(git, new UnityProjectService(), registry, new MountService(git, new UnityProjectService(), registry)), new UnityProjectService());
        var install = new BasisInstall { RepoRoot = project, UnityProjectPath = Path.Combine(project, "Proj"), Name = "Proj", IsGitRepo = true, HasUnityProject = true };

        var result = await sync.SyncAsync(install);

        Assert.False(result.Ok);
        Assert.True(result.HasConflicts);
        Assert.Equal("Proj/ProjectSettings/ProjectSettings.asset", Assert.Single(result.Conflicts));
        Assert.Contains("ProjectSettings.asset still has a merge conflict", result.Message);
        Assert.Equal(head, GitSandbox.Run(project, "rev-parse", "HEAD"));
    }

    [GitFact]
    public Task Edits_that_block_a_pull_are_backed_up_and_kept() => BlockedPull(keepMine: true);

    [GitFact]
    public Task Edits_that_block_a_pull_are_backed_up_and_replaced() => BlockedPull(keepMine: false);

    private static async Task BlockedPull(bool keepMine)
    {
        using var t = new TempDir("sync-blocked");
        var remote = t.CreateDir("remote");
        GitSandbox.Run(remote, "init", "-q", "-b", "developer");
        GitSandbox.Run(remote, "config", "core.autocrlf", "false");
        GitSandbox.Commit(remote, "base", ("Proj/a.asset", "base\n"), ("Proj/b.asset", "b\n"), ("Proj/gone.asset", "gone\n"), ("Proj/Packages/manifest.json", """{ "dependencies": {} }"""));
        var project = t.Combine("clone");
        GitSandbox.Run(t.Combine(), "clone", "-q", remote, project);
        GitSandbox.Run(project, "config", "core.autocrlf", "false");
        var tip = GitSandbox.Commit(remote, "theirs", ("Proj/a.asset", "theirs\n"), ("Proj/gone.asset", "theirs gone\n"), ("Proj/new.asset", "new\n"));
        File.WriteAllText(Path.Combine(project, "Proj", "a.asset"), "mine\n");
        File.WriteAllText(Path.Combine(project, "Proj", "b.asset"), "mine b\n");
        File.Delete(Path.Combine(project, "Proj", "gone.asset"));
        File.WriteAllText(Path.Combine(project, "Proj", "new.asset"), "my new\n");

        var git = new GitService(allowLocalRemotes: true);
        var registry = new MountRegistry(t.CreateDir("data"));
        var sync = new ProjectSyncService(git, new BasisDevService(git, new UnityProjectService(), registry, new MountService(git, new UnityProjectService(), registry)), new UnityProjectService());
        var install = new BasisInstall { RepoRoot = project, UnityProjectPath = Path.Combine(project, "Proj"), Name = "Proj", IsGitRepo = true, HasUnityProject = true };

        var blocked = await sync.SyncAsync(install);
        Assert.False(blocked.Ok);
        Assert.True(blocked.IsBlocked, blocked.Message);
        Assert.Equal(new[] { "Proj/a.asset", "Proj/gone.asset", "Proj/new.asset" }, blocked.Blocking);

        var result = await sync.SyncSettingAsideAsync(install, blocked.Blocking, keepMine);

        Assert.Equal(tip, GitSandbox.Run(project, "rev-parse", "HEAD"));
        Assert.Equal("mine b\n", File.ReadAllText(Path.Combine(project, "Proj", "b.asset")));
        Assert.Equal(keepMine ? "mine\n" : "theirs\n", File.ReadAllText(Path.Combine(project, "Proj", "a.asset")));
        Assert.Equal(keepMine ? "my new\n" : "new\n", File.ReadAllText(Path.Combine(project, "Proj", "new.asset")));
        Assert.Equal(!keepMine, File.Exists(Path.Combine(project, "Proj", "gone.asset")));
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(result.BackupFolder!, "Proj", "a.asset")));
        Assert.Equal("my new\n", File.ReadAllText(Path.Combine(result.BackupFolder!, "Proj", "new.asset")));
    }
}
