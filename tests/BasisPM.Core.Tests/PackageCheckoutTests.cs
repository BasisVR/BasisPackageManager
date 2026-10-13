using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageCheckoutTests
{
    private const string Url = "https://gitlab.com/example/toon.git";

    private static (GitSandbox Sandbox, MountService Service, string Clone) Repository(Action<GitSandbox> history, string checkout)
    {
        var sandbox = new GitSandbox();
        history(sandbox);
        var clone = sandbox.CloneProject("clone");
        GitSandbox.Run(clone, "checkout", "-q", checkout, "--");
        return (sandbox, new MountService(sandbox.Git, new UnityProjectService(), new MountRegistry(sandbox.Combine("data"))), clone);
    }

    private static void LegacyTagThenPackage(GitSandbox sandbox)
    {
        sandbox.CommitUpstream("legacy shaders", ("Assets/Toon.shader", "Shader \"Toon\" {}"));
        GitSandbox.Run(sandbox.Upstream, "tag", "v1.22");
        sandbox.CommitUpstream("make it a package", ("package.json", """{ "name": "com.example.toon", "version": "1.2.0" }"""));
    }

    [GitFact]
    public async Task An_auto_picked_release_without_a_package_json_falls_back_to_the_default_branch()
    {
        var (sandbox, service, clone) = Repository(LegacyTagThenPackage, "v1.22");
        using (sandbox)
        {
            Assert.Equal(Url, await service.PackageCheckoutAsync(clone, Url + "#v1.22", orDefaultBranch: true));
            Assert.True(File.Exists(Path.Combine(clone, "package.json")));
            Assert.Equal("developer", GitSandbox.Run(clone, "rev-parse", "--abbrev-ref", "HEAD"));
        }
    }

    [GitFact]
    public async Task A_chosen_release_without_a_package_json_is_refused_and_left_alone()
    {
        var (sandbox, service, clone) = Repository(LegacyTagThenPackage, "v1.22");
        using (sandbox)
        {
            Assert.Null(await service.PackageCheckoutAsync(clone, Url + "#v1.22", orDefaultBranch: false));
            Assert.False(File.Exists(Path.Combine(clone, "package.json")));
            Assert.Equal(GitSandbox.Run(clone, "rev-parse", "v1.22^{commit}"), GitSandbox.Run(clone, "rev-parse", "HEAD"));
        }
    }

    [GitFact]
    public async Task A_release_that_is_a_package_keeps_its_exact_manifest_line()
    {
        var (sandbox, service, clone) = Repository(s =>
        {
            s.CommitUpstream("package", ("package.json", """{ "name": "com.example.toon", "version": "1.3.0" }"""));
            GitSandbox.Run(s.Upstream, "tag", "v1.3.0");
            s.CommitUpstream("work in progress", ("Runtime/New.cs", "class New {}"));
        }, "v1.3.0");
        using (sandbox)
        {
            Assert.Equal("git+" + Url + "#v1.3.0", await service.PackageCheckoutAsync(clone, "git+" + Url + "#v1.3.0", orDefaultBranch: true));
            Assert.Equal(GitSandbox.Run(clone, "rev-parse", "v1.3.0^{commit}"), GitSandbox.Run(clone, "rev-parse", "HEAD"));
        }
    }

    [GitFact]
    public async Task A_sub_path_package_is_looked_for_in_its_folder()
    {
        var (sandbox, service, clone) = Repository(s =>
        {
            s.CommitUpstream("old layout", ("Assets/Toon.shader", "Shader \"Toon\" {}"));
            GitSandbox.Run(s.Upstream, "tag", "v2.0.0");
            s.CommitUpstream("package folder", ("Packages/com.example.toon/package.json", """{ "name": "com.example.toon", "version": "2.1.0" }"""));
        }, "v2.0.0");
        using (sandbox)
        {
            Assert.Equal(Url + "?path=Packages/com.example.toon",
                await service.PackageCheckoutAsync(clone, Url + "?path=Packages/com.example.toon#v2.0.0", orDefaultBranch: true));
            Assert.True(File.Exists(Path.Combine(clone, "Packages", "com.example.toon", "package.json")));
        }
    }

    [GitFact]
    public async Task Nothing_is_mounted_when_neither_the_release_nor_the_default_branch_is_a_package()
    {
        var (sandbox, service, clone) = Repository(s =>
        {
            s.CommitUpstream("legacy shaders", ("Assets/Toon.shader", "Shader \"Toon\" {}"));
            GitSandbox.Run(s.Upstream, "tag", "v1.22");
            s.CommitUpstream("more shaders", ("Assets/Outline.shader", "Shader \"Outline\" {}"));
        }, "v1.22");
        using (sandbox)
            Assert.Null(await service.PackageCheckoutAsync(clone, Url + "#v1.22", orDefaultBranch: true));
    }
}
