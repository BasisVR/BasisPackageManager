using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class GitExcludeTests
{
    [Fact]
    public void PatternFor_anchors_to_repo_root_so_only_the_mount_is_ignored()
    {
        var repo = @"C:\repo";
        Assert.Equal("/Basis/.basisdev/com.x/", GitExclude.PatternFor(repo, @"C:\repo\Basis\.basisdev\com.x"));
        Assert.Equal("/Basis/Packages/com.x/", GitExclude.PatternFor(repo, @"C:\repo\Basis\Packages\com.x"));
    }

    [Fact]
    public void PatternFor_is_null_when_the_folder_is_not_inside_the_repo()
    {
        Assert.Null(GitExclude.PatternFor(@"C:\repo", @"C:\repo"));         // the repo root itself
        Assert.Null(GitExclude.PatternFor(@"C:\repo", @"C:\elsewhere\x"));  // a sibling, not a child
    }

    [Fact]
    public void Add_then_Remove_round_trips_a_single_line()
    {
        using var t = new TempDir();
        t.CreateDir(".git");
        var mount = t.Combine("Basis/.basisdev/com.x");
        var excludePath = t.Combine(".git/info/exclude");

        GitExclude.Add(t.Path, mount);
        Assert.True(File.Exists(excludePath));
        Assert.Contains("/Basis/.basisdev/com.x/", File.ReadAllLines(excludePath));

        GitExclude.Remove(t.Path, mount);
        Assert.DoesNotContain("/Basis/.basisdev/com.x/", File.ReadAllLines(excludePath));
    }

    [Fact]
    public void Add_is_idempotent_and_preserves_existing_content()
    {
        using var t = new TempDir();
        t.WriteFile(".git/info/exclude", "# existing\nLibrary/\n");
        var mount = t.Combine("Basis/Packages/com.x");

        GitExclude.Add(t.Path, mount);
        GitExclude.Add(t.Path, mount);

        var lines = File.ReadAllLines(t.Combine(".git/info/exclude"));
        Assert.Equal(1, lines.Count(l => l == "/Basis/Packages/com.x/"));  // not duplicated
        Assert.Contains("Library/", lines);                               // template untouched
        Assert.Contains("# existing", lines);
    }

    [Fact]
    public void Remove_leaves_other_mounts_intact()
    {
        using var t = new TempDir();
        t.CreateDir(".git");
        var a = t.Combine("Basis/.basisdev/com.a");
        var b = t.Combine("Basis/.basisdev/com.b");

        GitExclude.Add(t.Path, a);
        GitExclude.Add(t.Path, b);
        GitExclude.Remove(t.Path, a);

        var lines = File.ReadAllLines(t.Combine(".git/info/exclude"));
        Assert.DoesNotContain("/Basis/.basisdev/com.a/", lines);
        Assert.Contains("/Basis/.basisdev/com.b/", lines);
    }

    [Fact]
    public void Add_is_a_no_op_when_the_repo_has_no_git_dir()
    {
        using var t = new TempDir();  // no .git present
        GitExclude.Add(t.Path, t.Combine("Basis/.basisdev/com.x"));
        Assert.False(Directory.Exists(t.Combine(".git")));
    }

    // ---- linked worktrees & submodules (.git is a "gitdir:" pointer file) ----

    [Fact]
    public void Add_and_Remove_use_the_resolved_common_dir_for_a_worktree()
    {
        using var t = new TempDir();
        // A linked worktree: .git is a pointer file; the shared info/exclude lives in a separate common dir.
        var commonDir = t.CreateDir("main/.git");
        var worktree = t.CreateDir("wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {t.Combine("main/.git/worktrees/wt")}\n");
        var mount = Path.Combine(worktree, "Basis", "Packages", "com.x");
        var excludePath = Path.Combine(commonDir, "info", "exclude");

        // The resolver stands in for `git rev-parse --git-common-dir`.
        GitExclude.Add(worktree, mount, _ => commonDir);
        Assert.True(File.Exists(excludePath));
        Assert.Contains("/Basis/Packages/com.x/", File.ReadAllLines(excludePath));

        GitExclude.Remove(worktree, mount, _ => commonDir);
        Assert.DoesNotContain("/Basis/Packages/com.x/", File.ReadAllLines(excludePath));
    }

    [Fact]
    public void Add_follows_the_commondir_file_when_git_cannot_answer_for_a_worktree()
    {
        using var t = new TempDir();
        var commonDir = t.CreateDir("main/.git");
        var perWorktreeDir = t.CreateDir("main/.git/worktrees/wt");
        // A worktree's per-worktree dir points back at the shared dir via a "commondir" file.
        File.WriteAllText(Path.Combine(perWorktreeDir, "commondir"), "../..\n");
        var worktree = t.CreateDir("wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {perWorktreeDir}\n");
        var mount = Path.Combine(worktree, "Basis", "Packages", "com.x");

        // resolver returns null (git present but unhelpful) OR is omitted — filesystem must still find it.
        GitExclude.Add(worktree, mount, _ => null);

        // "../.." from main/.git/worktrees/wt resolves to main/.git — the shared dir git actually reads.
        Assert.True(File.Exists(Path.Combine(commonDir, "info", "exclude")));
        Assert.Contains("/Basis/Packages/com.x/", File.ReadAllLines(Path.Combine(commonDir, "info", "exclude")));
        // Never the per-worktree dir, whose info/exclude git would ignore.
        Assert.False(File.Exists(Path.Combine(perWorktreeDir, "info", "exclude")));
    }

    [Fact]
    public void Add_uses_the_pointed_dir_for_a_submodule_which_has_no_commondir_file()
    {
        using var t = new TempDir();
        var moduleGitDir = t.CreateDir("super/.git/modules/sub");   // submodule git dir — no commondir file
        var submodule = t.CreateDir("super/sub");
        File.WriteAllText(Path.Combine(submodule, ".git"), $"gitdir: {moduleGitDir}\n");
        var mount = Path.Combine(submodule, "Basis", "Packages", "com.x");

        GitExclude.Add(submodule, mount, commonDirResolver: null);

        var excludePath = Path.Combine(moduleGitDir, "info", "exclude");
        Assert.True(File.Exists(excludePath));
        Assert.Contains("/Basis/Packages/com.x/", File.ReadAllLines(excludePath));
    }

    [Fact]
    public void Add_prefers_the_resolver_over_the_filesystem_commondir()
    {
        using var t = new TempDir();
        // Two candidates: the git-reported dir and the commondir-file dir. The resolver must win.
        var reported = t.CreateDir("reported.git");
        var commonDir = t.CreateDir("main/.git");
        var perWorktreeDir = t.CreateDir("main/.git/worktrees/wt");
        File.WriteAllText(Path.Combine(perWorktreeDir, "commondir"), "../..\n");
        var worktree = t.CreateDir("wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {perWorktreeDir}\n");
        var mount = Path.Combine(worktree, "Basis", "Packages", "com.x");

        GitExclude.Add(worktree, mount, _ => reported);

        Assert.True(File.Exists(Path.Combine(reported, "info", "exclude")));
        Assert.False(File.Exists(Path.Combine(commonDir, "info", "exclude")));
    }
}
