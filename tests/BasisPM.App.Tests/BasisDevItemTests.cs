using System.ComponentModel;
using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class BasisDevItemTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "basispm-item-tests", "Basis");
    private static readonly string Project = Path.Combine(Root, "Basis");

    private static BasisInstall Install(bool basisCheckout = true) => new()
    {
        RepoRoot = Root,
        UnityProjectPath = Project,
        Name = "Basis",
        IsGitRepo = true,
        HasUnityProject = true,
        IsBasisCheckout = basisCheckout,
    };

    private static BasisDevPackage Shadowed() => new()
    {
        Id = "dev.x",
        Source = PackageSourceKind.ProjectRepo,
        EmbeddedFolder = Path.Combine(Project, "Packages", "dev.x"),
        EmbeddedTracked = true,
        CloneFolder = Path.Combine(Project, ".basisdev", "dev.x"),
        CloneExists = true,
        Sidecar = new BasisDevSidecar
        {
            Package = "dev.x",
            Folder = ".basisdev/dev.x",
            Upstream = new BasisDevUpstream { Url = "https://github.com/o/r.git", Ref = "basis", Path = "Basis/Packages/dev.x", Commit = "4a5354e40c1d" },
        },
        Issues = new[] { BasisDevIssueKind.Shadowed },
        Comparison = new BasisDevComparison(false, 4, 0, 33, null),
    };

    [AvaloniaFact]
    public void Shadowed_clone_names_both_copies_and_offers_release_or_leaving_it_to_the_basis_repo()
    {
        Localizer.Instance.SetLanguage("en");
        var item = new BasisDevItem(Shadowed(), Install());

        Assert.Equal("Basis repo: Packages/dev.x", item.SourceText);
        Assert.Equal(".basisdev/dev.x (not in use)", item.CloneText);
        Assert.Equal("https://github.com/o/r.git ?path=Basis/Packages/dev.x #basis @ 4a5354e40", item.UpstreamText);
        Assert.Equal("37 file(s) differ from the clone: 4 only here, 0 only upstream, 33 changed. This copy matches no upstream commit, so it has changes of its own.", item.ComparisonText);
        Assert.Equal("Clone not in use", item.IssueLabel);
        Assert.Equal("Unity loads Packages/dev.x from the project, so the clone in .basisdev/dev.x isn't used.", item.IssueText);
        Assert.Equal(new[] { BasisDevAction.Release, BasisDevAction.Ignore }, item.Choices.Select(c => c.Action));
        Assert.True(item.Choices[0].IsDestructive);
        Assert.False(item.Choices[0].IsPrimary);
        Assert.Equal("Handled by Basis repo", item.Choices[1].Label);
        Assert.False(item.Choices[1].IsDestructive);
    }

    [AvaloniaFact]
    public void Project_repo_label_follows_the_kind_of_project()
    {
        Localizer.Instance.SetLanguage("en");
        Assert.Equal("Project repo: Packages/dev.x", new BasisDevItem(Shadowed(), Install(basisCheckout: false)).SourceText);
    }

    [AvaloniaFact]
    public void Unrecorded_detached_clone_lists_use_first_and_marks_the_upstream_as_unrecorded()
    {
        Localizer.Instance.SetLanguage("en");
        var package = new BasisDevPackage
        {
            Id = "com.v",
            Source = PackageSourceKind.Git,
            ManifestValue = "https://github.com/v/u.git?path=com.v#28c3b8d9",
            CloneFolder = Path.Combine(Project, ".basisdev", "com.v"),
            CloneExists = true,
            PackageRoot = Path.Combine(Project, ".basisdev", "com.v", "com.v"),
            Proposed = new BasisDevSidecar { Package = "com.v", Folder = ".basisdev/com.v", Upstream = new BasisDevUpstream { Url = "https://github.com/v/u.git", Path = "com.v", Commit = "b933e9322aa" } },
            Issues = new[] { BasisDevIssueKind.Detached, BasisDevIssueKind.Unrecorded },
        };

        var item = new BasisDevItem(package, Install());

        Assert.Equal("Git: https://github.com/v/u.git?path=com.v#28c3b8d9", item.SourceText);
        Assert.Equal("https://github.com/v/u.git ?path=com.v @ b933e9322 (not recorded yet)", item.UpstreamText);
        Assert.Equal(new[] { BasisDevAction.UseClone, BasisDevAction.Release, BasisDevAction.Ignore, BasisDevAction.Record }, item.Choices.Select(c => c.Action));
        Assert.True(item.Choices[0].IsPrimary);
        Assert.Contains("manifest.json points at https://github.com/v/u.git?path=com.v#28c3b8d9", item.IssueText);
        Assert.Contains(".basisdev/com.v has no .basisdev sidecar yet", item.IssueText);
    }

    [AvaloniaFact]
    public void Healthy_package_has_a_source_but_no_issue_or_choices()
    {
        Localizer.Instance.SetLanguage("en");
        var item = new BasisDevItem(new BasisDevPackage { Id = "com.r", Source = PackageSourceKind.Registry, ManifestValue = "1.2.3" }, Install());

        Assert.Equal("Registry: 1.2.3", item.SourceText);
        Assert.False(item.HasIssue);
        Assert.False(item.HasClone);
        Assert.False(item.HasChoices);
    }

    [AvaloniaFact]
    public void Row_raises_its_issue_properties_when_the_item_arrives()
    {
        Localizer.Instance.SetLanguage("en");
        var row = new PackageRow(new CatalogPackageVersion { Name = "dev.x", DisplayName = "X", Version = "1.0.0", Description = "" }, installedVersion: null);
        var raised = new List<string?>();
        PropertyChangedEventHandler track = (_, e) => raised.Add(e.PropertyName);
        row.PropertyChanged += track;

        row.BasisDev = new BasisDevItem(Shadowed(), Install());

        row.PropertyChanged -= track;
        Assert.True(row.HasBasisDev);
        Assert.True(row.HasBasisDevIssue);
        Assert.Equal("Clone not in use", row.BasisDevIssueLabel);
        Assert.Contains(nameof(PackageRow.HasBasisDevIssue), raised);
        Assert.Contains(nameof(PackageRow.BasisDevIssueLabel), raised);
    }

    [AvaloniaFact]
    public void Clone_left_to_the_basis_repo_shows_no_issue_and_can_be_flagged_again()
    {
        Localizer.Instance.SetLanguage("en");
        var sidecar = Shadowed().Sidecar!;
        sidecar.HandledByProject = true;
        var item = new BasisDevItem(Shadowed() with { Sidecar = sidecar, Issues = Array.Empty<BasisDevIssueKind>() }, Install());

        Assert.False(item.HasIssue);
        Assert.Equal(".basisdev/dev.x (not used, handled by the Basis repo)", item.CloneText);
        Assert.Equal(new[] { BasisDevAction.Unignore, BasisDevAction.Release }, item.Choices.Select(c => c.Action));
        Assert.Equal("Flag again", item.Choices[0].Label);
        Assert.False(item.Choices[0].IsPrimary);
        Assert.Equal(".basisdev/dev.x (not used, handled by the project)", new BasisDevItem(Shadowed() with { Sidecar = sidecar, Issues = Array.Empty<BasisDevIssueKind>() }, Install(basisCheckout: false)).CloneText);
    }
}
