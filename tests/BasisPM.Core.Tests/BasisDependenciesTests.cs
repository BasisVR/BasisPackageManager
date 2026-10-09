using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class BasisDependenciesTests
{
    private const string BasisManifest = """{ "dependencies": { "com.vendor.utils": "https://github.com/vendor/utils.git#abc123", "com.unity.timeline": "1.0.0" } }""";
    private const string ProjectManifest = """{ "dependencies": { "com.vendor.utils": "https://github.com/vendor/utils.git#abc123", "com.unity.timeline": "1.0.0", "com.community.addon": "https://github.com/someone/addon.git" } }""";
    private const string WorkingManifest = """{ "dependencies": { "com.vendor.utils": "https://github.com/vendor/utils.git#abc123", "com.unity.timeline": "1.0.0", "com.community.addon": "https://github.com/someone/addon.git", "com.community.wip": "file:../../wip" } }""";

    [GitFact]
    public async Task Lists_what_basis_declares_and_not_what_the_project_added()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest));
        var project = box.CloneProject();
        GitSandbox.Commit(project, "my add-on", ("Basis/Packages/manifest.json", ProjectManifest));
        GitSandbox.Write(project, "Basis/Packages/manifest.json", WorkingManifest);

        var ids = await box.Updates.GetBasisDependenciesAsync(Path.Combine(project, "Basis"));

        Assert.Equal(new[] { "com.unity.timeline", "com.vendor.utils" }, ids.Order(StringComparer.Ordinal));
    }

    [GitFact]
    public async Task Ignores_basis_changes_the_project_has_not_merged()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest));
        var project = box.CloneProject();
        box.CommitUpstream("newer basis", ("Basis/Packages/manifest.json", """{ "dependencies": { "com.vendor.utils": "https://github.com/vendor/utils.git#def456", "com.vendor.newer": "https://github.com/vendor/newer.git" } }"""));
        GitSandbox.Run(project, "fetch", "-q", "origin");

        var ids = await box.Updates.GetBasisDependenciesAsync(Path.Combine(project, "Basis"));

        Assert.Contains("com.unity.timeline", ids);
        Assert.DoesNotContain("com.vendor.newer", ids);
    }

    [GitFact]
    public async Task Uses_the_fetched_basis_ref_when_origin_is_a_fork()
    {
        using var box = new GitSandbox();
        var basis = box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest));
        var project = box.CloneProject();
        GitSandbox.Run(project, "remote", "set-url", "origin", "https://example.com/someone/Basis.git");
        GitSandbox.Commit(project, "my add-on", ("Basis/Packages/manifest.json", ProjectManifest));

        Assert.Contains("com.community.addon", await box.Updates.GetBasisDependenciesAsync(Path.Combine(project, "Basis")));

        GitSandbox.Run(project, "update-ref", BasisUpdateService.UpstreamRefPrefix + "developer", basis);
        var ids = await box.Updates.GetBasisDependenciesAsync(Path.Combine(project, "Basis"));

        Assert.Contains("com.vendor.utils", ids);
        Assert.DoesNotContain("com.community.addon", ids);
    }

    [GitFact]
    public async Task Uses_the_recorded_basis_commit_for_a_rehosted_copy()
    {
        using var box = new GitSandbox();
        var basis = box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest));
        var project = box.Combine("rehosted");
        Directory.CreateDirectory(project);
        GitSandbox.Run(project, "init", "-q", "-b", "main");
        GitSandbox.Commit(project, "import", ("Packages/manifest.json", ProjectManifest));
        GitSandbox.Run(project, "fetch", "-q", box.Upstream, "+refs/heads/developer:" + BasisUpdateService.UpstreamRefPrefix + "developer");

        Assert.Contains("com.community.addon", await box.Updates.GetBasisDependenciesAsync(project));

        GitSandbox.Run(project, "commit", "-q", "--allow-empty", "-m", $"Record Basis\n\n{BasisUpdateService.CommitTrailer}: {basis}\n{BasisUpdateService.FolderTrailer}: Basis -> .");
        var ids = await box.Updates.GetBasisDependenciesAsync(project);

        Assert.Equal(new[] { "com.unity.timeline", "com.vendor.utils" }, ids.Order(StringComparer.Ordinal));
    }

    [GitFact]
    public async Task Uses_the_estimated_basis_commit_for_a_copy_never_updated()
    {
        using var box = new GitSandbox();
        var basis = box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest));
        var project = box.Combine("copy");
        Directory.CreateDirectory(project);
        GitSandbox.Run(project, "init", "-q", "-b", "main");
        GitSandbox.Commit(project, "import", ("Packages/manifest.json", ProjectManifest));
        GitSandbox.Run(project, "fetch", "-q", box.Upstream, "+refs/heads/developer:" + BasisUpdateService.UpstreamRefPrefix + "developer");
        File.WriteAllText(Path.Combine(project, ".git", BasisUpdateService.EstimateFileName),
            $$"""{ "localBranch": "main", "basisCommit": "{{basis}}", "basisBranch": "developer", "layout": "Basis -> ." }""");

        var ids = await box.Updates.GetBasisDependenciesAsync(project);

        Assert.Equal(new[] { "com.unity.timeline", "com.vendor.utils" }, ids.Order(StringComparer.Ordinal));
    }

    [GitFact]
    public async Task Lists_the_package_folders_basis_ships_and_not_the_projects_own()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest), ("Basis/Packages/com.basis.framework/package.json", "{}"));
        var project = box.CloneProject();
        GitSandbox.Commit(project, "studio tools", ("Basis/Packages/com.studio.tools/package.json", "{}"));
        GitSandbox.Write(project, "Basis/Packages/com.studio.sketch/package.json", "{}");

        var shipped = await box.Updates.GetBasisPackagesAsync(Path.Combine(project, "Basis"));

        Assert.NotNull(shipped.EmbeddedFolders);
        Assert.Contains("com.basis.framework", shipped.EmbeddedFolders);
        Assert.DoesNotContain("com.studio.tools", shipped.EmbeddedFolders);
        Assert.DoesNotContain("com.studio.sketch", shipped.EmbeddedFolders);
        Assert.Contains("com.vendor.utils", shipped.Dependencies);
    }

    [GitFact]
    public async Task Reads_the_shipped_folders_of_a_rehosted_copy_at_its_recorded_basis_commit()
    {
        using var box = new GitSandbox();
        var basis = box.CommitUpstream("basis", ("Basis/Packages/manifest.json", BasisManifest), ("Basis/Packages/com.basis.framework/package.json", "{}"));
        var project = box.Combine("rehosted");
        Directory.CreateDirectory(project);
        GitSandbox.Run(project, "init", "-q", "-b", "main");
        GitSandbox.Commit(project, "import", ("Packages/manifest.json", ProjectManifest), ("Packages/com.basis.framework/package.json", "{}"), ("Packages/com.studio.tools/package.json", "{}"));
        GitSandbox.Run(project, "fetch", "-q", box.Upstream, "+refs/heads/developer:" + BasisUpdateService.UpstreamRefPrefix + "developer");
        GitSandbox.Run(project, "commit", "-q", "--allow-empty", "-m", $"Record Basis\n\n{BasisUpdateService.CommitTrailer}: {basis}\n{BasisUpdateService.FolderTrailer}: Basis -> .");

        var shipped = await box.Updates.GetBasisPackagesAsync(project);

        Assert.Contains("com.basis.framework", shipped.EmbeddedFolders!);
        Assert.DoesNotContain("com.studio.tools", shipped.EmbeddedFolders!);
    }

    [GitFact]
    public async Task Reads_a_unity_project_at_the_repository_root()
    {
        using var box = new GitSandbox();
        box.CommitUpstream("basis", ("Packages/manifest.json", BasisManifest));
        var project = box.CloneProject();

        Assert.Contains("com.vendor.utils", await box.Updates.GetBasisDependenciesAsync(project));
    }

    [Fact]
    public async Task Is_empty_for_a_project_outside_git()
    {
        using var temp = new TempDir("basis-deps");
        temp.WriteFile("Packages/manifest.json", BasisManifest);

        Assert.Empty(await new BasisUpdateService(new GitService()).GetBasisDependenciesAsync(temp.Path));
        Assert.Null((await new BasisUpdateService(new GitService()).GetBasisPackagesAsync(temp.Path)).EmbeddedFolders);
    }
}
