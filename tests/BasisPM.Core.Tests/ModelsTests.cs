using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ModelsTests
{
    [Theory]
    [InlineData(GitChangeKind.Modified, "modified")]
    [InlineData(GitChangeKind.Added, "added")]
    [InlineData(GitChangeKind.Deleted, "deleted")]
    [InlineData(GitChangeKind.Renamed, "renamed")]
    [InlineData(GitChangeKind.Untracked, "new")]
    [InlineData(GitChangeKind.Conflicted, "conflict")]
    [InlineData(GitChangeKind.Other, "changed")]
    public void GitFileChange_KindLabel(GitChangeKind kind, string label)
    {
        Assert.Equal(label, new GitFileChange("XY", "path", kind, false).KindLabel);
    }

    [Fact]
    public void GitStatus_clean_and_change_count()
    {
        var clean = new GitStatus("main", "abc1234", Array.Empty<GitFileChange>(), AheadBehind.None);
        Assert.True(clean.IsClean);
        Assert.Equal(0, clean.ChangeCount);

        var dirty = new GitStatus("main", "abc1234",
            new[] { new GitFileChange(" M", "a", GitChangeKind.Modified, false) }, AheadBehind.None);
        Assert.False(dirty.IsClean);
        Assert.Equal(1, dirty.ChangeCount);
    }

    [Fact]
    public void AheadBehind_none_has_no_upstream()
    {
        Assert.False(AheadBehind.None.HasUpstream);
        Assert.False(AheadBehind.None.IsUpToDate);
    }

    [Theory]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 1, 0, false)]
    [InlineData(true, 0, 2, false)]
    [InlineData(false, 0, 0, false)]
    public void AheadBehind_IsUpToDate(bool hasUpstream, int ahead, int behind, bool upToDate)
    {
        Assert.Equal(upToDate, new AheadBehind(hasUpstream, ahead, behind).IsUpToDate);
    }

    [Fact]
    public void GitResult_carries_ok_code_and_output()
    {
        var r = new GitResult(false, 128, "fatal: nope");
        Assert.False(r.Ok);
        Assert.Equal(128, r.Code);
        Assert.Equal("fatal: nope", r.Output);
    }

    [Fact]
    public void BasisInstall_display_name_prefers_alias()
    {
        var withAlias = new BasisInstall { RepoRoot = "r", UnityProjectPath = "r", Name = "Folder", Alias = "Nice Name" };
        Assert.Equal("Nice Name", withAlias.DisplayName);

        var noAlias = new BasisInstall { RepoRoot = "r", UnityProjectPath = "r", Name = "Folder" };
        Assert.Equal("Folder", noAlias.DisplayName);
    }

    [Fact]
    public void DetectionResult_ok_and_fail()
    {
        var ok = DetectionResult.Ok("/path", "resolved");
        Assert.True(ok.IsValid);
        Assert.Equal("/path", ok.ResolvedPath);

        var fail = DetectionResult.Fail("nope");
        Assert.False(fail.IsValid);
        Assert.Null(fail.ResolvedPath);
        Assert.Equal("nope", fail.Reason);
    }

    [Fact]
    public void ContributeResult_success_and_fail()
    {
        var ok = ContributeResult.Success("https://github.com/o/r/pull/1", forked: true);
        Assert.True(ok.Ok);
        Assert.Equal("https://github.com/o/r/pull/1", ok.PrUrl);
        Assert.True(ok.Forked);
        Assert.Null(ok.Error);

        var fail = ContributeResult.Fail("boom");
        Assert.False(fail.Ok);
        Assert.Equal("boom", fail.Error);
        Assert.Null(fail.PrUrl);
    }

    [Fact]
    public void Bundle_has_sensible_defaults()
    {
        var b = new Bundle();
        Assert.Equal("", b.Id);
        Assert.NotNull(b.Tags);
        Assert.NotNull(b.Packages);
        Assert.Empty(b.Packages);
    }

    [Fact]
    public void PackageManifest_defaults_to_empty_dependencies()
    {
        Assert.Empty(new PackageManifest().Dependencies);
    }
}
