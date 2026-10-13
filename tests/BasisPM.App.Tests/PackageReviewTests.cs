using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackageReviewTests : IDisposable
{
    private const string Id = "com.example.tool";
    private const string Commit = "abcdef1234567890abcdef1234567890abcdef12";
    private const string Other = "1234567890abcdef1234567890abcdef12345678";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"review-{Guid.NewGuid():N}");

    public PackageReviewTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Editor"));
        File.WriteAllText(Path.Combine(_root, "package.json"), $$"""{ "name": "{{Id}}", "displayName": "Example Tool", "version": "1.2.0" }""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private PackageReview Review(string id = Id, string? declaredName = Id) =>
        new(id, _root, _root, UpmGitUrl.Parse("https://github.com/example/tool.git?path=Packages/tool#v1.2.0")!, Commit)
        {
            Package = new UpmPackageJson { Name = declaredName ?? "", DisplayName = "Example Tool", Version = "1.2.0" },
        };

    private static PackageScan Scan(params (PackageCapability Capability, PackageRisk Risk, string File, int Line, string Evidence)[] findings) =>
        new(findings.Select(f => new PackageFinding(f.Capability, f.Risk, f.File, f.Line, f.Evidence)).ToList(), 3, 1, 12, false);

    private static PackageReviewViewModel Model(PackageReview review, InstallOrigin origin, PackageScan scan, PackageAdvisory? advisory = null,
        IReadOnlyList<PackageIssue>? issues = null, bool trusted = false, string? pinned = null, string? claimedName = null) =>
        new(review, origin, trusted, pinned, scan, advisory, issues ?? Array.Empty<PackageIssue>(), claimedName);

    [AvaloniaFact]
    public void High_risk_findings_make_cancel_the_default_and_list_the_worst_first()
    {
        Localizer.Instance.SetLanguage("en");
        var scan = Scan(
            (PackageCapability.RunsAutomatically, PackageRisk.Caution, "Editor/Boot.cs", 4, "[InitializeOnLoad]"),
            (PackageCapability.StartsPrograms, PackageRisk.High, "Editor/Boot.cs", 9, "Process.Start"),
            (PackageCapability.StartsPrograms, PackageRisk.High, "Editor/More.cs", 1, "Process.Start"),
            (PackageCapability.StartsPrograms, PackageRisk.High, "Editor/More.cs", 2, "Process.Start"),
            (PackageCapability.StartsPrograms, PackageRisk.High, "Editor/More.cs", 3, "Process.Start"),
            (PackageCapability.StartsPrograms, PackageRisk.High, "Editor/More.cs", 4, "Process.Start"));

        var model = Model(Review(), InstallOrigin.GitUrl, scan);

        Assert.True(model.ShowSafety);
        Assert.True(model.IsRisky);
        Assert.True(model.CancelIsPrimary);
        Assert.False(model.InstallIsPrimary);
        Assert.Equal("Install anyway", model.InstallLabel);
        Assert.Equal(L.Tr("review.reason.gitUrl"), model.Reason);
        var first = model.Capabilities[0];
        Assert.True(first.IsHigh);
        Assert.Equal(L.Tr("review.cap.StartsPrograms.title"), first.Title);
        Assert.Equal("Editor/Boot.cs:9  ·  Process.Start", first.Places[0]);
        Assert.Equal(4, first.Places.Count);
        Assert.True(first.HasMore);
        Assert.False(model.Capabilities[1].IsHigh);
        Assert.Equal("github.com/example/tool  ·  Packages/tool", model.SourceText);
        Assert.Equal("1.2.0  ·  v1.2.0  ·  abcdef1234", model.VersionText);
        Assert.True(model.HasFooter);
    }

    [AvaloniaFact]
    public void A_clean_package_from_a_catalog_you_added_installs_by_default_but_never_claims_it_is_safe()
    {
        Localizer.Instance.SetLanguage("en");
        var model = Model(Review(), InstallOrigin.Unofficial, PackageScan.Empty);

        Assert.False(model.IsRisky);
        Assert.True(model.InstallIsPrimary);
        Assert.Equal("Install", model.InstallLabel);
        Assert.True(model.NothingFound);
        Assert.Contains("doesn't prove", model.NothingFoundText);
    }

    [AvaloniaFact]
    public void A_link_to_an_unlisted_package_is_always_treated_as_risky_and_flags_a_different_name()
    {
        Localizer.Instance.SetLanguage("en");
        var model = Model(Review(), InstallOrigin.Link, PackageScan.Empty, claimedName: "Official Basis Avatar Tools");

        Assert.True(model.IsRisky);
        Assert.True(model.HasNameWarning);
        Assert.Contains("Official Basis Avatar Tools", model.NameWarning);
        Assert.Contains("Example Tool", model.NameWarning);
    }

    [AvaloniaFact]
    public void A_package_json_name_that_differs_from_the_install_id_is_called_out()
    {
        Localizer.Instance.SetLanguage("en");
        var model = Model(Review(id: "com.basis.framework"), InstallOrigin.PackageList, PackageScan.Empty);

        Assert.True(model.HasNameWarning);
        Assert.Contains(Id, model.NameWarning);
        Assert.Contains("com.basis.framework", model.NameWarning);
    }

    [AvaloniaFact]
    public void A_pulled_package_cannot_be_installed_from_the_review()
    {
        Localizer.Instance.SetLanguage("en");
        var advisory = new PackageAdvisory { Package = Id, Title = "Steals Discord tokens", Details = "Removed 2026-10-14." };

        var model = Model(Review(), InstallOrigin.Registry, PackageScan.Empty, advisory, trusted: true);

        Assert.True(model.ShowSafety);
        Assert.False(model.CanInstall);
        Assert.True(model.AdvisoryBlocks);
        Assert.Equal(L.Tr("review.advisory.pulled"), model.AdvisoryHeading);
        Assert.True(model.HasAdvisoryDetails);
    }

    [AvaloniaFact]
    public void A_registry_version_nobody_checked_explains_which_commit_was_checked()
    {
        Localizer.Instance.SetLanguage("en");
        var model = Model(Review(), InstallOrigin.Registry, PackageScan.Empty, pinned: Other);

        Assert.Contains("1234567890", model.Reason);
        Assert.Contains("abcdef1234", model.Reason);
    }

    [AvaloniaFact]
    public void Compatibility_errors_sort_first_and_make_install_an_explicit_choice()
    {
        Localizer.Instance.SetLanguage("en");
        var issues = new[]
        {
            new PackageIssue("readme-missing", IssueSeverity.Info, Id, "No README."),
            new PackageIssue("assembly-duplicate", IssueSeverity.Error, Id, "Two assemblies are called Example.") { File = "Runtime/Example.asmdef", Fix = "Rename one of them." },
            new PackageIssue("unity-newer", IssueSeverity.Warning, Id, "Needs a newer Unity."),
        };

        var model = Model(Review(), InstallOrigin.Registry, PackageScan.Empty, issues: issues, trusted: true);

        Assert.False(model.ShowSafety);
        Assert.Equal(new[] { IssueSeverity.Error, IssueSeverity.Warning, IssueSeverity.Info }, model.Issues.Select(i => i.Severity));
        Assert.Equal("Rename one of them.", model.Issues[0].Fix);
        Assert.True(model.HasIssueErrors);
        Assert.Equal("Install anyway", model.InstallLabel);
        Assert.Equal(L.Tr("review.reason.compatibility"), model.Reason);
    }

    [AvaloniaFact]
    public async Task The_gate_trusts_registry_packages_and_stops_anything_else_for_review()
    {
        Localizer.Instance.SetLanguage("en");
        var vm = new MainWindowViewModel().PackagesVM;
        var target = new BasisInstall { RepoRoot = _root, UnityProjectPath = _root, Name = "Project", HasUnityProject = true };
        var review = Review();

        Assert.True(await vm.ReviewGate(target, InstallOrigin.Registry)(review));
        Assert.True(await vm.ReviewGate(target, InstallOrigin.Registry, new CatalogPackageVersion { Name = Id, ReviewedCommit = Commit[..10] })(review));
        Assert.True(await vm.ReviewGate(target, InstallOrigin.Registry, new CatalogPackageVersion { Name = Id, Commit = Other })(review));
        Assert.False(await vm.ReviewGate(target, InstallOrigin.Registry, new CatalogPackageVersion { Name = Id, ReviewedCommit = Other })(review));
        Assert.False(await vm.ReviewGate(target, InstallOrigin.Unofficial)(review));
        Assert.False(await vm.ReviewGate(target, InstallOrigin.GitUrl)(review));
        Assert.False(await vm.ReviewGate(target, InstallOrigin.Link)(review));

        vm.SetAdvisories(new[] { new PackageAdvisory { Package = Id, Title = "Pulled" } });
        Assert.False(await vm.ReviewGate(target, InstallOrigin.Registry)(review));
        vm.SetAdvisories(new[] { new PackageAdvisory { Package = Id, Title = "Old release only", Commits = new() { Other } } });
        Assert.True(await vm.ReviewGate(target, InstallOrigin.Registry)(review));
    }

    [AvaloniaFact]
    public async Task A_pulled_package_in_the_open_project_gets_a_banner_and_its_row_is_flagged()
    {
        Localizer.Instance.SetLanguage("en");
        var project = Path.Combine(_root, "Proj");
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
        File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1\n");
        File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), $$"""{ "dependencies": { "{{Id}}": "https://github.com/example/tool.git#v1.2.0", "com.example.fine": "https://github.com/example/fine.git" } }""");
        var info = await new UnityProjectService().LoadAsync(project);
        var install = new BasisInstall { RepoRoot = project, UnityProjectPath = project, Name = "Proj", HasUnityProject = true, Manifest = info.Manifest };
        var vm = new MainWindowViewModel().PackagesVM;
        vm.SetActiveInstall(install);

        vm.SetAdvisories(new[]
        {
            new PackageAdvisory { Package = Id, Repository = "https://github.com/example/tool", Title = "Uploads saved logins" },
            new PackageAdvisory { Package = "com.example.fine", Repository = "https://github.com/someone-else/fine", Title = "A different repository" },
        });

        var pulled = Assert.Single(vm.PulledPackages);
        Assert.Equal(Id, pulled.Id);
        Assert.True(vm.HasBlockingPulledPackages);
        Assert.Equal(L.Tr("advisory.banner.title"), vm.PulledTitle);

        vm.SetAdvisories(Array.Empty<PackageAdvisory>());
        Assert.False(vm.HasPulledPackages);
    }

    [AvaloniaFact]
    public void An_automatic_api_upgrade_is_described_without_marking_the_row_as_edited()
    {
        Localizer.Instance.SetLanguage("en");
        var row = new PackageRow(new CatalogPackageVersion { Name = Id, DisplayName = "Example Tool", Version = "1.2.0", Url = "https://github.com/example/tool.git" }, installedVersion: null, isMounted: true);

        row.ApiUpgrade = new ApiUpgradeRecord
        {
            Unity = "6000.7.0b4",
            Changes = { new ApiUpgradeChange { File = "Editor/Importer.cs", Line = 84, From = "CreateAssetWithContent", To = "CreateAssetWithTextContent" } },
        };

        Assert.True(row.HasApiUpgrade);
        Assert.False(row.IsChanged);
        Assert.Equal("Applied automatic API upgrade for Unity 6000.7.0b4.", row.ApiUpgradeText);
        Assert.Equal("CreateAssetWithContent became CreateAssetWithTextContent in Editor/Importer.cs:84", row.ApiUpgradeDetails);
    }

    [Theory]
    [InlineData("https://github.com/example/tool.git#v1.2.0", Commit, "https://github.com/example/tool.git#" + Commit)]
    [InlineData("https://github.com/example/tool.git?path=Packages/tool", Commit, "https://github.com/example/tool.git?path=Packages/tool#" + Commit)]
    [InlineData("https://github.com/example/tool.git#v1.2.0", null, "https://github.com/example/tool.git#v1.2.0")]
    [InlineData("https://github.com/example/tool.git#v1.2.0", "--upload-pack=x", "https://github.com/example/tool.git#v1.2.0")]
    [InlineData(null, Commit, null)]
    public void Registry_dependencies_are_pinned_to_the_reviewed_commit(string? url, string? reviewed, string? expected)
        => Assert.Equal(expected, PackageTrust.PinnedUrl(new CatalogPackageVersion { Name = Id, Url = url, ReviewedCommit = reviewed, Commit = Other }));
}
