using System.Xml.Linq;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ServerPackageServiceTests
{
    private const string CoreGroup = "'$(MSBuildProjectName)' == 'BasisNetworkCore'";
    private const string ConsoleGroup = "'$(MSBuildProjectName)' == 'BasisNetworkConsole'";

    private sealed class GitScratch : IDisposable
    {
        public TempDir Dir { get; } = new("server-packages");
        public void Dispose()
        {
            try { BasisInstallService.DeleteFolderAsync(Dir.Path).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting {Dir.Path}", ex); }
            Dir.Dispose();
        }
    }

    private static string NewRepo(TempDir t, bool stitching = true)
    {
        t.WriteFile("repo/Basis Server/BasisNetworkCore/BasisNetworkCore.csproj", "<Project />");
        if (stitching) t.WriteFile("repo/Basis Server/Directory.Build.targets", "<Project><Import Project=\"Packages/packages-lock.props\" /></Project>");
        return t.Combine("repo");
    }

    private static void WriteTransport(string root, string id = "com.test.transport", string version = "1.0.0")
    {
        GitSandbox.Write(root, "package.json", $"{{ \"name\": \"{id}\", \"version\": \"{version}\", \"displayName\": \"Test Transport\" }}");
        GitSandbox.Write(root, "Network/BasisNetworkCore.asmref", "{ \"reference\": \"BasisNetworkCore\" }");
        GitSandbox.Write(root, "Network/TestTransport.cs", "public sealed class TestTransport { }");
        GitSandbox.Write(root, "Network/Unity/Test.Transport.Unity.asmdef", "{ \"name\": \"Test.Transport.Unity\" }");
        GitSandbox.Write(root, "Network/Unity/UnityOnly.cs", "public sealed class UnityOnly { }");
        GitSandbox.Write(root, "Server~/BasisNetworkConsole/TestCommands.cs", "public static class TestCommands { }");
    }

    private static string NewUpstream(TempDir t, string name = "upstream")
    {
        var upstream = t.CreateDir(name);
        GitSandbox.Run(upstream, "init", "-q", "-b", "main");
        WriteTransport(upstream);
        GitSandbox.Commit(upstream, "first");
        return upstream;
    }

    private static XElement Group(XDocument lockFile, string condition) =>
        lockFile.Root!.Elements("ItemGroup").Single(g => (string?)g.Attribute("Condition") == condition);

    [Fact]
    public void Declaration_uses_explicit_modules_and_nuget()
    {
        using var t = new TempDir();
        t.WriteFile("pkg/package.json", """
            {
              "name": "com.test.explicit",
              "version": "2.1.0",
              "displayName": "Explicit",
              "basisServer": {
                "modules": [
                  { "path": "Shared", "assembly": "basisnetworkcore" },
                  { "path": "Server~/Commands", "assembly": "BasisNetworkConsole" }
                ],
                "nuget": { "Some.Library": "1.2.3" }
              }
            }
            """);
        t.CreateDir("pkg/Shared");
        t.CreateDir("pkg/Server~/Commands");
        t.WriteFile("pkg/Other/BasisNetworkServer.asmref", "{ \"reference\": \"BasisNetworkServer\" }");

        var declaration = ServerPackageService.ReadDeclaration(t.Combine("pkg"));

        Assert.True(declaration.IsValid, string.Join(" ", declaration.Problems));
        Assert.Equal("com.test.explicit", declaration.Id);
        Assert.Equal("2.1.0", declaration.Version);
        Assert.Equal(new[] { ("Shared", "BasisNetworkCore"), ("Server~/Commands", "BasisNetworkConsole") },
            declaration.Modules.Select(m => (m.Path, m.Assembly)));
        Assert.Equal("1.2.3", declaration.NuGet["Some.Library"]);
    }

    [Fact]
    public void Declaration_rejects_unknown_assemblies_escaping_paths_and_bad_nuget()
    {
        using var t = new TempDir();
        t.WriteFile("pkg/package.json", """
            {
              "name": "com.test.bad",
              "basisServer": {
                "modules": [
                  { "path": "A", "assembly": "Assembly-CSharp" },
                  { "path": "../outside", "assembly": "BasisNetworkCore" },
                  { "path": "Missing", "assembly": "BasisNetworkCore" }
                ],
                "nuget": { "-bad": "1.0.0", "Good.Id": "1.0.0;evil" }
              }
            }
            """);
        t.CreateDir("pkg/A");

        var declaration = ServerPackageService.ReadDeclaration(t.Combine("pkg"));

        Assert.False(declaration.IsValid);
        Assert.Contains(declaration.Problems, p => p.Contains("Assembly-CSharp"));
        Assert.Contains(declaration.Problems, p => p.Contains("stay inside"));
        Assert.Contains(declaration.Problems, p => p.Contains("Missing"));
        Assert.Contains(declaration.Problems, p => p.Contains("-bad"));
        Assert.Contains(declaration.Problems, p => p.Contains("Good.Id"));
        Assert.Empty(declaration.NuGet);
    }

    [Fact]
    public void Declaration_discovers_asmrefs_by_name_and_guid_and_server_only_folders()
    {
        using var t = new TempDir();
        t.WriteFile("pkg/package.json", "{ \"name\": \"com.test.discovered\" }");
        t.WriteFile("pkg/Core/Ref.asmref", "{ \"reference\": \"BasisNetworkCore\" }");
        t.WriteFile("pkg/Server/Ref.asmref", "{ \"reference\": \"GUID:b7ce407150fbecd4c84e6256f87d3364\" }");
        t.WriteFile("pkg/Elsewhere/Ref.asmref", "{ \"reference\": \"Basis Framework\" }");
        t.WriteFile("pkg/Hidden~/Ref.asmref", "{ \"reference\": \"BasisNetworkClient\" }");
        t.CreateDir("pkg/Server~/BasisNetworkConsole");
        t.CreateDir("pkg/Server~/BasisServerTests");
        t.CreateDir("pkg/Server~/NotAHost");

        var declaration = ServerPackageService.ReadDeclaration(t.Combine("pkg"));

        Assert.True(declaration.IsValid, string.Join(" ", declaration.Problems));
        Assert.Equal(new[] { ("Core", "BasisNetworkCore"), ("Server", "BasisNetworkServer"), ("Server~/BasisNetworkConsole", "BasisNetworkConsole"), ("Server~/BasisServerTests", "BasisServerTests") },
            declaration.Modules.Select(m => (m.Path, m.Assembly)).OrderBy(m => m.Path, StringComparer.Ordinal));
    }

    [Fact]
    public void Declaration_leaves_out_nested_assembly_folders_and_inner_modules()
    {
        using var t = new TempDir();
        t.WriteFile("pkg/package.json", "{ \"name\": \"com.test.nested\" }");
        t.WriteFile("pkg/Runtime/Core.asmref", "{ \"reference\": \"BasisNetworkCore\" }");
        t.WriteFile("pkg/Runtime/Server/Server.asmref", "{ \"reference\": \"BasisNetworkServer\" }");
        t.WriteFile("pkg/Runtime/Unity/Glue/Unity.asmdef", "{ \"name\": \"Unity\" }");
        t.WriteFile("pkg/Runtime/Plain/Code.cs", "class Code { }");

        var declaration = ServerPackageService.ReadDeclaration(t.Combine("pkg"));
        var runtime = declaration.Modules.Single(m => m.Path == "Runtime");

        Assert.Equal(new[] { "Server", "Unity/Glue" }, runtime.Excludes);
        Assert.Empty(declaration.Modules.Single(m => m.Path == "Runtime/Server").Excludes);
    }

    [Fact]
    public void Declaration_without_server_code_is_invalid()
    {
        using var t = new TempDir();
        t.WriteFile("pkg/package.json", "{ \"name\": \"com.test.unityonly\" }");
        t.WriteFile("pkg/Runtime/Unity.asmdef", "{ \"name\": \"Unity\" }");

        var declaration = ServerPackageService.ReadDeclaration(t.Combine("pkg"));

        Assert.False(declaration.IsValid);
        Assert.Contains(declaration.Problems, p => p.Contains("no server code"));
        Assert.False(ServerPackageService.ReadDeclaration(t.Combine("nothing")).IsValid);
    }

    [Theory]
    [InlineData("com.basis.transport.websocket", true)]
    [InlineData("com.example.a-b_c", true)]
    [InlineData("Com.Upper", false)]
    [InlineData("../escape", false)]
    [InlineData("com..dots", false)]
    [InlineData("", false)]
    public void Ids_follow_unity_package_naming(string id, bool valid) => Assert.Equal(valid, ServerPackageService.IsValidId(id));

    [Fact]
    public void Lock_compiles_modules_into_their_host_projects()
    {
        var entry = new ServerPackageLockEntry("com.test.transport", "1.0.0", "https://github.com/o/r.git#v1", "https://github.com/o/r.git", "v1",
            "0123456789abcdef0123456789abcdef01234567", null, null,
            new[] { new ServerPackageModule("Network", "BasisNetworkCore", new[] { "Unity" }), new ServerPackageModule("Server~/BasisNetworkConsole", "BasisNetworkConsole", Array.Empty<string>()), new ServerPackageModule("Server~/BasisServerTests", "BasisServerTests", Array.Empty<string>()) },
            new Dictionary<string, string>());

        var lockFile = XDocument.Parse(ServerPackageService.BuildLock(new[] { entry }));
        var key = ServerPackageService.PropertyKey("com.test.transport");
        var root = $"$(BasisServerPackageRoot_{key})";

        var property = lockFile.Root!.Element("PropertyGroup")!.Element("BasisServerPackageRoot_" + key)!;
        Assert.Equal("$(MSBuildThisFileDirectory)com.test.transport/", property.Value);
        var core = Group(lockFile, CoreGroup);
        var compile = core.Element("Compile")!;
        Assert.Equal(root + "Network/**/*.cs", (string?)compile.Attribute("Include"));
        Assert.Contains(root + "Network/Unity/**", ((string)compile.Attribute("Exclude")!).Split(';'));
        Assert.Contains(root + "Network/**/obj/**", ((string)compile.Attribute("Exclude")!).Split(';'));
        Assert.Equal($"!Exists('{root}package.json')", (string?)core.Element("BasisServerPackageMissing")!.Attribute("Condition"));
        var console = Group(lockFile, ConsoleGroup);
        Assert.Equal(root + "Server~/BasisNetworkConsole/**/*.cs", (string?)console.Element("Compile")!.Attribute("Include"));
        var metadata = console.Element("AssemblyMetadata")!;
        Assert.Equal("BasisServerPackage:com.test.transport", (string?)metadata.Attribute("Include"));
        Assert.Equal("1.0.0 (0123456)", (string?)metadata.Attribute("Value"));
        var tests = Group(lockFile, "'$(MSBuildProjectName)' == 'BasisServerTests'");
        Assert.Equal(root + "Server~/BasisServerTests/**/*.cs", (string?)tests.Element("Compile")!.Attribute("Include"));
        Assert.Null(tests.Element("AssemblyMetadata"));
        Assert.DoesNotContain(lockFile.Root.Elements("ItemGroup"), g => ((string?)g.Attribute("Condition"))?.Contains("BasisNetworkServer") == true);
        var package = lockFile.Descendants("BasisServerPackage").Single();
        Assert.Equal("$(MSBuildThisFileDirectory)com.test.transport/", (string?)package.Attribute("Clone"));
        Assert.Equal($"$(BasisServerPackageLinked_{key})", (string?)package.Attribute("Linked"));
    }

    [Fact]
    public void Lock_round_trips_and_escapes_msbuild_characters()
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var entry = new ServerPackageLockEntry("com.test.odd", "1.0.0-beta+meta", "file:../Odd $(Folder);@x", null, null, null, null, "../Odd $(Folder);@x",
            new[] { new ServerPackageModule("Code 50%", "BasisNetworkServer", new[] { "Skip'd" }) },
            new Dictionary<string, string> { ["Some.Library"] = "[1.0,2.0)" });
        var text = ServerPackageService.BuildLock(new[] { entry });
        Directory.CreateDirectory(ServerPackageService.PackagesDirectory(repo));
        File.WriteAllText(ServerPackageService.LockPath(repo), text);

        Assert.DoesNotContain("$(Folder)", text);
        Assert.Contains("Code 50%25/**/*.cs", text);
        Assert.Contains("../Odd %24(Folder)%3B%40x/", text);
        var read = new ServerPackageService(new GitService()).LoadLock(repo)["com.test.odd"];
        Assert.Equal(entry.Source, read.Source);
        Assert.Equal(entry.LocalPath, read.LocalPath);
        Assert.Equal("1.0.0-beta+meta", read.Version);
        Assert.Equal("Code 50%", read.Modules.Single().Path);
        Assert.Equal(new[] { "Skip'd" }, read.Modules.Single().Excludes);
        Assert.Equal("[1.0,2.0)", read.NuGet["Some.Library"]);
    }

    [Fact]
    public void Lock_merges_nuget_references_to_the_highest_version_per_host()
    {
        ServerPackageLockEntry Entry(string id, string version) => new(id, "1.0.0", "file:" + id, null, null, null, null, id,
            new[] { new ServerPackageModule("", "BasisNetworkCore", Array.Empty<string>()) }, new Dictionary<string, string> { ["Shared.Lib"] = version });

        var lockFile = XDocument.Parse(ServerPackageService.BuildLock(new[] { Entry("com.test.a", "1.10.0"), Entry("com.test.b", "1.9.0") }));
        var reference = Group(lockFile, CoreGroup).Elements("PackageReference").Single();

        Assert.Equal("Shared.Lib", (string?)reference.Attribute("Include"));
        Assert.Equal("1.10.0", (string?)reference.Attribute("Version"));
    }

    [Fact]
    public void Property_keys_are_valid_and_distinct()
    {
        var a = ServerPackageService.PropertyKey("com.a-b");
        var b = ServerPackageService.PropertyKey("com.a_b");
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_]+$", a);
        Assert.Equal(a, ServerPackageService.PropertyKey("com.a-b"));
    }

    [Fact]
    public async Task Install_refuses_a_server_without_package_support()
    {
        using var t = new TempDir();
        var repo = NewRepo(t, stitching: false);
        var service = new ServerPackageService(new GitService());

        Assert.False(ServerPackageService.SupportsPackages(repo));
        var result = await service.InstallAsync(repo, "https://github.com/o/r.git");
        Assert.False(result.Ok);
        Assert.Contains("Directory.Build.targets", result.Message);
        Assert.False((await service.InstallAsync(t.Combine("no-server"), "https://github.com/o/r.git")).Ok);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ext::sh -c evil")]
    [InlineData("https://github.com/o/r.git#--upload-pack=evil")]
    [InlineData("https://github.com/o/r.git?path=../escape")]
    public async Task Install_refuses_unsafe_or_unknown_sources(string source)
    {
        using var t = new TempDir();
        var repo = NewRepo(t);
        var result = await new ServerPackageService(new GitService()).InstallAsync(repo, source);
        Assert.False(result.Ok);
        Assert.False(Directory.Exists(ServerPackageService.PackagesDirectory(repo)) && Directory.EnumerateDirectories(ServerPackageService.PackagesDirectory(repo)).Any());
    }

    [GitFact]
    public async Task Install_update_restore_and_remove_a_git_package()
    {
        using var scratch = new GitScratch();
        var t = scratch.Dir;
        var repo = NewRepo(t);
        var upstream = NewUpstream(t);
        var first = GitSandbox.Run(upstream, "rev-parse", "HEAD");
        var service = new ServerPackageService(new GitService(allowLocalRemotes: true));

        var installed = await service.InstallAsync(repo, upstream, "com.test.transport");
        Assert.True(installed.Ok, installed.Message);
        var clone = Path.Combine(ServerPackageService.PackagesDirectory(repo), "com.test.transport");
        Assert.True(File.Exists(Path.Combine(clone, "package.json")));
        Assert.Equal(upstream, service.LoadManifest(repo).Dependencies["com.test.transport"]);
        var locked = service.LoadLock(repo)["com.test.transport"];
        Assert.Equal(first, locked.Commit);
        Assert.Equal(new[] { "BasisNetworkConsole", "BasisNetworkCore" }, locked.Modules.Select(m => m.Assembly).OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { "Unity" }, locked.Modules.Single(m => m.Assembly == "BasisNetworkCore").Excludes);
        var listed = Assert.Single(await service.ListAsync(repo));
        Assert.Equal(ServerPackageStatus.Ready, listed.Status);
        Assert.Equal("Test Transport", listed.DisplayName);
        Assert.False((await service.InstallAsync(repo, upstream)).Ok);

        WriteTransport(upstream, version: "1.1.0");
        var second = GitSandbox.Commit(upstream, "second");
        var updated = await service.UpdateAsync(repo, "com.test.transport");
        Assert.True(updated.Ok, updated.Message);
        Assert.Equal(second, service.LoadLock(repo)["com.test.transport"].Commit);
        Assert.Equal("1.1.0", service.LoadLock(repo)["com.test.transport"].Version);

        var pinned = await service.UpdateAsync(repo, "com.test.transport", first);
        Assert.True(pinned.Ok, pinned.Message);
        Assert.Equal(upstream + "#" + first, service.LoadManifest(repo).Dependencies["com.test.transport"]);
        Assert.Equal(first, service.LoadLock(repo)["com.test.transport"].Commit);

        await BasisInstallService.DeleteFolderAsync(clone);
        Assert.Equal(ServerPackageStatus.NotRestored, Assert.Single(await service.ListAsync(repo)).Status);
        GitSandbox.Commit(upstream, "third", ("extra.txt", "x"));
        var restored = await service.RestoreAsync(repo);
        Assert.True(restored.Ok, restored.Message);
        Assert.Equal(first, GitSandbox.Run(clone, "rev-parse", "HEAD"));
        Assert.Equal(first, service.LoadLock(repo)["com.test.transport"].Commit);

        var removed = await service.RemoveAsync(repo, "com.test.transport");
        Assert.True(removed.Ok, removed.Message);
        Assert.False(Directory.Exists(clone));
        Assert.Empty(service.LoadManifest(repo).Dependencies);
        Assert.Empty(service.LoadLock(repo));
    }

    [GitFact]
    public async Task Remove_and_update_keep_local_edits_unless_forced()
    {
        using var scratch = new GitScratch();
        var t = scratch.Dir;
        var repo = NewRepo(t);
        var upstream = NewUpstream(t);
        var service = new ServerPackageService(new GitService(allowLocalRemotes: true));
        Assert.True((await service.InstallAsync(repo, upstream)).Ok);
        var clone = Path.Combine(ServerPackageService.PackagesDirectory(repo), "com.test.transport");
        File.AppendAllText(Path.Combine(clone, "Network", "TestTransport.cs"), "\n// edited");

        Assert.Equal(ServerPackageStatus.Changed, Assert.Single(await service.ListAsync(repo)).Status);
        Assert.False((await service.UpdateAsync(repo, "com.test.transport")).Ok);
        var refused = await service.RemoveAsync(repo, "com.test.transport");
        Assert.False(refused.Ok);
        Assert.Contains("local edits", refused.Message);
        Assert.True(Directory.Exists(clone));

        Assert.True((await service.RemoveAsync(repo, "com.test.transport", discardLocalWork: true)).Ok);
        Assert.False(Directory.Exists(clone));
    }

    [GitFact]
    public async Task File_packages_and_links_point_the_build_at_local_folders()
    {
        using var scratch = new GitScratch();
        var t = scratch.Dir;
        var repo = NewRepo(t);
        var upstream = NewUpstream(t);
        var service = new ServerPackageService(new GitService(allowLocalRemotes: true));
        WriteTransport(t.CreateDir("repo/Basis/Packages/com.test.local"), "com.test.local");

        var local = await service.InstallAsync(repo, "file:../../Basis/Packages/com.test.local");
        Assert.True(local.Ok, local.Message);
        var localLock = service.LoadLock(repo)["com.test.local"];
        Assert.Equal("../../Basis/Packages/com.test.local", localLock.LocalPath);
        Assert.Null(localLock.Url);
        Assert.Contains("$(MSBuildThisFileDirectory)../../Basis/Packages/com.test.local/", File.ReadAllText(ServerPackageService.LockPath(repo)));

        var workingCopy = t.Combine("unity-copy");
        GitSandbox.Run(t.Path, "clone", "-q", upstream, workingCopy);
        var linked = await service.InstallLinkedAsync(repo, upstream, workingCopy);
        Assert.True(linked.Ok, linked.Message);
        Assert.False(Directory.Exists(Path.Combine(ServerPackageService.PackagesDirectory(repo), "com.test.transport")));
        Assert.Equal(GitSandbox.Run(workingCopy, "rev-parse", "HEAD"), service.LoadLock(repo)["com.test.transport"].Commit);
        var localProps = File.ReadAllText(ServerPackageService.LocalPath(repo));
        Assert.Contains("BasisServerPackageLinked_" + ServerPackageService.PropertyKey("com.test.transport"), localProps);
        Assert.Contains(workingCopy.Replace('\\', '/'), localProps);
        var info = (await service.ListAsync(repo)).Single(p => p.Id == "com.test.transport");
        Assert.True(info.IsLinked);
        Assert.Equal(ServerPackageStatus.Ready, info.Status);

        var unlinked = await service.UnlinkAsync(repo, "com.test.transport");
        Assert.True(unlinked.Ok, unlinked.Message);
        Assert.False(File.Exists(ServerPackageService.LocalPath(repo)));
        Assert.Equal(ServerPackageStatus.NotRestored, (await service.ListAsync(repo)).Single(p => p.Id == "com.test.transport").Status);
        Assert.True((await service.RestoreAsync(repo)).Ok);
        Assert.Equal(ServerPackageStatus.Ready, (await service.ListAsync(repo)).Single(p => p.Id == "com.test.transport").Status);
    }
}
