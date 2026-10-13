using System.Net;
using System.Text;
using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageAuthoringTests
{
    private sealed class AuthoringProject : IDisposable
    {
        private readonly TempDir _temp = new("authoring");

        public AuthoringProject(string dependencies = "{}", bool git = true)
        {
            Repo = _temp.CreateDir("repo");
            Unity = _temp.CreateDir("repo/Proj");
            _temp.CreateDir("repo/Proj/Assets");
            _temp.WriteFile("repo/Proj/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.2.7f1\n");
            _temp.WriteFile("repo/Proj/Packages/manifest.json", "{ \"dependencies\": " + dependencies + " }");
            if (git)
            {
                GitSandbox.Run(Repo, "init", "-q", "-b", "main");
                GitSandbox.Run(Repo, "add", "-A");
                GitSandbox.Run(Repo, "commit", "-q", "-m", "project");
            }
            Registry = new MountRegistry(_temp.CreateDir("data"));
            Mounts = new MountService(Git, new UnityProjectService(), Registry);
            Scaffold = new PackageScaffoldService(Git, new UnityProjectService(), Mounts);
        }

        public string Repo { get; }
        public string Unity { get; }
        public GitService Git { get; } = new(allowLocalRemotes: true, retry: GitRetryPolicy.None);
        public MountRegistry Registry { get; }
        public MountService Mounts { get; }
        public PackageScaffoldService Scaffold { get; }

        public BasisInstall Install(bool basisCheckout = false) => new()
        {
            RepoRoot = Repo,
            UnityProjectPath = Unity,
            Name = "Proj",
            IsGitRepo = true,
            HasUnityProject = true,
            IsBasisCheckout = basisCheckout,
            UnityVersion = "6000.2.7f1",
        };

        public string Combine(string relative) => _temp.Combine("repo/Proj/" + relative);
        public string Temp(string relative) => _temp.Combine(relative);
        public string Write(string relative, string content) => _temp.WriteFile("repo/Proj/" + relative, content);

        public Dictionary<string, string> Manifest() =>
            JsonDocument.Parse(File.ReadAllText(Combine("Packages/manifest.json"))).RootElement.GetProperty("dependencies")
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

        public void Dispose() => _temp.Dispose();
    }

    private static PackageScaffoldRequest Request(string id = "com.studio.seats", ScaffoldLicense license = ScaffoldLicense.Mit) => new()
    {
        Id = id,
        DisplayName = "Seats",
        Description = "Chairs people can sit in.",
        Author = "Studio",
        License = license,
        Unity = "6000.2",
        BasisFrom = "2026-09-16",
        References = new[] { "Basis Framework", "BasisSDK" },
        Server = true,
        Samples = true,
        Year = 2026,
    };

    [Theory]
    [InlineData("com.studio.seats", PackageIdProblem.None)]
    [InlineData("dev.hai-vr.basis_tools", PackageIdProblem.None)]
    [InlineData("", PackageIdProblem.Missing)]
    [InlineData("Com.Studio.Seats", PackageIdProblem.Characters)]
    [InlineData("com.studio..seats", PackageIdProblem.Characters)]
    [InlineData("com.studio.seats.", PackageIdProblem.Characters)]
    [InlineData("com studio", PackageIdProblem.Characters)]
    [InlineData("seats", PackageIdProblem.NoDot)]
    [InlineData("com.unity.seats", PackageIdProblem.Reserved)]
    public void Package_ids_follow_the_upm_naming_rules(string id, PackageIdProblem expected) =>
        Assert.Equal(expected, PackageScaffoldService.ValidateId(id));

    [Theory]
    [InlineData("com.studio.my-feature", "Studio.MyFeature")]
    [InlineData("dev.hai-vr.basis.ndmf", "HaiVr.Basis.Ndmf")]
    [InlineData("org.basisvr.3d-tools", "Basisvr.Pkg3dTools")]
    [InlineData("studio.seats", "Studio.Seats")]
    public void Assembly_names_come_from_the_id_without_the_domain(string id, string expected) =>
        Assert.Equal(expected, PackageScaffoldService.AssemblyName(id));

    [Fact]
    public void Suggested_ids_slug_the_author_and_name()
    {
        Assert.Equal("com.doola-studio.cool-seats", PackageScaffoldService.SuggestId("Doola Studio", "Cool Seats!"));
        Assert.Equal("com.mystudio.package", PackageScaffoldService.SuggestId("", ""));
        Assert.Equal("6000.2", PackageScaffoldService.UnityMajorMinor("6000.2.7f1"));
        Assert.Null(PackageScaffoldService.UnityMajorMinor("unknown"));
    }

    [Fact]
    public void Scaffold_writes_a_complete_package()
    {
        var files = PackageScaffoldService.CreateFiles(Request());

        Assert.Equal(new[]
        {
            "CHANGELOG.md", "Editor/Studio.Seats.Editor.asmdef", "LICENSE", "README.md", "Runtime/SeatsBehaviour.cs", "Runtime/Studio.Seats.asmdef",
            "Samples~/Example/README.md", "Server~/BasisNetworkServer/SeatsServerPackage.cs", "package.json",
        }, files.Keys.ToArray());
        var package = PackageJsonFile.Parse(files["package.json"])!;
        Assert.Equal("com.studio.seats", package.Name);
        Assert.Equal("Seats", package.DisplayName);
        Assert.Equal("0.1.0", package.Version);
        Assert.Equal("6000.2", package.Unity);
        Assert.Equal("MIT", package.License);
        Assert.Equal("Studio", package.Author!.Name);
        Assert.Equal("2026-09-16", package.Basis!.From);
        Assert.DoesNotContain("\r", files["package.json"]);
        using (var samples = JsonDocument.Parse(files["package.json"]))
            Assert.Equal("Samples~/Example", samples.RootElement.GetProperty("samples")[0].GetProperty("path").GetString());

        using (var runtime = JsonDocument.Parse(files["Runtime/Studio.Seats.asmdef"]))
        {
            Assert.Equal("Studio.Seats", runtime.RootElement.GetProperty("name").GetString());
            Assert.Equal(new[] { "Basis Framework", "BasisSDK" }, runtime.RootElement.GetProperty("references").EnumerateArray().Select(r => r.GetString()).ToArray());
            Assert.Equal(0, runtime.RootElement.GetProperty("includePlatforms").GetArrayLength());
        }
        using (var editor = JsonDocument.Parse(files["Editor/Studio.Seats.Editor.asmdef"]))
        {
            Assert.Equal("Editor", editor.RootElement.GetProperty("includePlatforms")[0].GetString());
            Assert.Equal("Studio.Seats", editor.RootElement.GetProperty("references")[0].GetString());
        }
        Assert.Contains("[assembly: BasisNetworkPackage(typeof(Studio.Seats.Server.SeatsServerPackage))]", files["Server~/BasisNetworkServer/SeatsServerPackage.cs"]);
        Assert.Contains("namespace Studio.Seats.Server\n{", files["Server~/BasisNetworkServer/SeatsServerPackage.cs"]);
        Assert.StartsWith("MIT License\n\nCopyright (c) 2026 Studio\n", files["LICENSE"]);
        Assert.Contains("`Server~/`", files["README.md"]);
    }

    [Fact]
    public void Scaffold_leaves_out_the_parts_that_were_not_asked_for()
    {
        var files = PackageScaffoldService.CreateFiles(Request(license: ScaffoldLicense.None) with { Runtime = false, Editor = false, Server = false, Samples = false, BasisFrom = null });

        Assert.Equal(new[] { "CHANGELOG.md", "README.md", "package.json" }, files.Keys.ToArray());
        using var json = JsonDocument.Parse(files["package.json"]);
        Assert.False(json.RootElement.TryGetProperty("license", out _));
        Assert.False(json.RootElement.TryGetProperty("basis", out _));
        Assert.Contains("Apache License, Version 2.0", PackageScaffoldService.LicenseText(ScaffoldLicense.Apache2, "Studio", 2026));
    }

    [Fact]
    public void Basis_assemblies_are_found_without_tests()
    {
        using var p = new AuthoringProject(git: false);
        p.Write("Packages/com.basis.framework/package.json", "{ \"name\": \"com.basis.framework\", \"version\": \"0.0.1\" }");
        p.Write("Packages/com.basis.framework/Basis Framework.asmdef", "{ \"name\": \"Basis Framework\" }");
        p.Write("Packages/com.basis.framework/Editor/Basis Framework.Editor.asmdef", "{ \"name\": \"Basis Framework.Editor\", \"includePlatforms\": [\"Editor\"] }");
        p.Write("Packages/com.basis.framework/Tests/Basis.Tests.asmdef", "{ \"name\": \"Basis.Tests\" }");
        p.Write("Packages/com.basis.framework/Runtime/Test.asmdef", "{ \"name\": \"Basis.Runtime.Tests\", \"defineConstraints\": [\"UNITY_INCLUDE_TESTS\"] }");
        p.Write("Packages/com.other/package.json", "{ \"name\": \"com.other\", \"version\": \"1.0.0\" }");
        p.Write("Packages/com.other/Other.asmdef", "{ \"name\": \"Other\" }");

        var assemblies = PackageScaffoldService.FindBasisAssemblies(p.Unity);

        Assert.Equal(new[] { ("Basis Framework", false), ("Basis Framework.Editor", true) }, assemblies.Select(a => (a.Name, a.EditorOnly)).ToArray());
    }

    [GitFact]
    public async Task Create_makes_a_git_repository_in_basisdev_and_loads_it()
    {
        using var p = new AuthoringProject();

        var result = await p.Scaffold.CreateAsync(p.Install(), Request());

        Assert.True(result.Ok, result.Message);
        var folder = p.Combine(".basisdev/com.studio.seats");
        Assert.Equal(folder, result.Folder);
        Assert.True(MountService.IsWorkingClone(folder));
        Assert.Equal("Create com.studio.seats", GitSandbox.Run(folder, "log", "-1", "--format=%s"));
        Assert.Equal("", GitSandbox.Status(folder));
        Assert.Equal("file:../.basisdev/com.studio.seats", p.Manifest()["com.studio.seats"]);
        Assert.Equal(".basisdev/com.studio.seats", BasisDevStore.Read(p.Unity, "com.studio.seats")!.Folder);
        Assert.False(File.Exists(BasisPackagesStore.FilePath(p.Unity)));
        var scan = (await new BasisDevService(p.Git, new UnityProjectService(), p.Registry, p.Mounts).ScanAsync(p.Install())).Find("com.studio.seats")!;
        Assert.True(scan.CloneActive);
        Assert.Empty(scan.Issues);
        Assert.DoesNotContain(".basisdev", GitSandbox.Status(p.Repo));

        var again = await p.Scaffold.CreateAsync(p.Install(), Request());
        Assert.False(again.Ok);
        Assert.Contains("already exists", again.Message);
    }

    [GitFact]
    public async Task Create_refuses_an_id_the_project_already_has()
    {
        using var p = new AuthoringProject("{ \"com.studio.seats\": \"1.0.0\" }");

        var result = await p.Scaffold.CreateAsync(p.Install(), Request());

        Assert.False(result.Ok);
        Assert.Contains("manifest.json already has com.studio.seats", result.Message);
        Assert.False(Directory.Exists(p.Combine(".basisdev/com.studio.seats")));
    }

    [Theory]
    [InlineData("1.2.3", VersionBump.Patch, "1.2.4")]
    [InlineData("1.2.3", VersionBump.Minor, "1.3.0")]
    [InlineData("1.2.3", VersionBump.Major, "2.0.0")]
    [InlineData("1.2.3-beta.1", VersionBump.Patch, "1.2.3")]
    [InlineData("nonsense", VersionBump.Patch, null)]
    public void Versions_bump(string current, VersionBump bump, string? expected) =>
        Assert.Equal(expected, PackagePublishService.NextVersion(current, bump));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("", false)]
    public void Release_versions_need_three_numbers(string version, bool expected) =>
        Assert.Equal(expected, PackagePublishService.IsValidVersion(version));

    [Fact]
    public void Tag_style_follows_the_repository()
    {
        Assert.Equal("v", PackagePublishService.DetectTagPrefix(Array.Empty<string>()));
        Assert.Equal("v", PackagePublishService.DetectTagPrefix(new[] { "v1.0.0", "v1.1.0", "1.2.0" }));
        Assert.Equal("", PackagePublishService.DetectTagPrefix(new[] { "1.0.0", "1.1.0", "latest" }));
    }

    [Fact]
    public void Setting_the_version_keeps_the_file_as_written()
    {
        var text = "{\n    \"name\": \"com.studio.seats\",\n    \"version\": \"1.0.0\",\n    \"dependencies\": { \"com.other\": \"1.0.0\" }\n}\n";

        Assert.Equal(text.Replace("\"version\": \"1.0.0\"", "\"version\": \"1.1.0\""), PackagePublishService.SetVersion(text, "1.1.0"));
        var added = PackagePublishService.SetVersion("{ \"name\": \"com.a.b\", \"displayName\": \"B\" }", "0.2.0");
        using var json = JsonDocument.Parse(added);
        Assert.Equal(new[] { "name", "version", "displayName" }, json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Changelog_entries_go_above_the_previous_release()
    {
        var date = new DateOnly(2026, 10, 14);
        Assert.Equal("# Changelog\n\n## [1.0.0] - 2026-10-14\n\n- First\n", PackagePublishService.PrependChangelog(null, "1.0.0", date, "- First"));
        var existing = "# Changelog\n\nAll notable changes.\n\n## [1.0.0] - 2026-01-01\n\n- First\n";
        Assert.Equal("# Changelog\n\nAll notable changes.\n\n## [1.1.0] - 2026-10-14\n\n- Second\n\n## [1.0.0] - 2026-01-01\n\n- First\n",
            PackagePublishService.PrependChangelog(existing, "1.1.0", date, "- Second"));
        Assert.Equal("# Changelog\n\nAll notable changes.\n\n## [0.1.0] - 2026-10-14\n",
            PackagePublishService.PrependChangelog("# Changelog\n\nAll notable changes.\n", "0.1.0", date, ""));
    }

    [Fact]
    public void Suggested_notes_skip_releases_and_merges()
    {
        var when = DateTimeOffset.UnixEpoch;
        var notes = PackagePublishService.SuggestNotes(new[]
        {
            new GitCommitInfo("a", "a", "x", when, "Fix seat height"),
            new GitCommitInfo("b", "b", "x", when, "Release 1.0.0"),
            new GitCommitInfo("c", "c", "x", when, "Merge branch 'main'"),
            new GitCommitInfo("d", "d", "x", when, "Fix seat height"),
            new GitCommitInfo("e", "e", "x", when, "Add arm rests"),
        });
        Assert.Equal("- Fix seat height\n- Add arm rests", notes);
    }

    [Fact]
    public void Basic_checks_catch_the_problems_that_block_a_release()
    {
        using var p = new AuthoringProject(git: false);
        var root = p.Combine("Pkg");
        Directory.CreateDirectory(root);
        Assert.Equal(IssueCodes.ManifestMissing, Assert.Single(PackagePublishService.BasicChecks(root)).Code);

        p.Write("Pkg/package.json", "{ not json");
        Assert.Equal(IssueCodes.ManifestInvalid, Assert.Single(PackagePublishService.BasicChecks(root)).Code);

        p.Write("Pkg/package.json", "{ \"name\": \"Bad Name\", \"version\": \"1.0\", \"unity\": \"6000\" }");
        var issues = PackagePublishService.BasicChecks(root);
        Assert.Equal(new[] { IssueCodes.NameInvalid, IssueCodes.VersionInvalid, IssueCodes.UnityInvalid }, issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => i.Code).ToArray());
        Assert.Equal(new[] { IssueCodes.DisplayNameMissing, IssueCodes.DescriptionMissing, IssueCodes.LicenseMissing, IssueCodes.ReadmeMissing },
            issues.Where(i => i.Severity == IssueSeverity.Warning).Select(i => i.Code).ToArray());

        p.Write("Pkg/package.json", "{ \"name\": \"com.a.b\", \"displayName\": \"B\", \"description\": \"d\", \"version\": \"1.0.0\", \"unity\": \"6000.2\", \"license\": \"MIT\" }");
        p.Write("Pkg/README.md", "# B\n");
        Assert.Empty(PackagePublishService.BasicChecks(root));
        Assert.Equal(IssueCodes.NameMismatch, Assert.Single(PackagePublishService.BasicChecks(root, "com.a.c")).Code);
    }

    private static readonly GitHubUser Tester = new() { Login = "tester", Id = 42, Name = "Tester" };

    private sealed class FakeGitHub
    {
        public List<(HttpMethod Method, string Path, string? Body)> Calls { get; } = new();
        public Func<HttpMethod, string, (HttpStatusCode, string)?> Respond { get; set; } = (_, _) => null;

        public GitHubApiService Api() => new(new HttpClient(new StubHttpMessageHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            Calls.Add((request.Method, path, body));
            return Respond(request.Method, path) is { } answer
                ? StubHttpMessageHandler.Json(answer.Item1, answer.Item2)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        })));
    }

    [GitFact]
    public async Task Publish_tags_pushes_and_releases()
    {
        using var p = new AuthoringProject();
        var created = await p.Scaffold.CreateAsync(p.Install(), Request());
        Assert.True(created.Ok, created.Message);
        var folder = created.Folder!;
        GitSandbox.Run(folder, "remote", "add", "origin", "https://github.com/tester/seats.git");
        var bare = p.Temp("seats.git");
        GitSandbox.Run(p.Temp(""), "init", "-q", "--bare", bare);
        File.WriteAllText(Path.Combine(folder, "Runtime", "Seat.cs"), "class Seat { }\n");
        var github = new FakeGitHub
        {
            Respond = (method, path) => method == HttpMethod.Post && path == "repos/tester/seats/releases"
                ? (HttpStatusCode.Created, "{ \"tag_name\": \"v0.1.0\", \"html_url\": \"https://github.com/tester/seats/releases/tag/v0.1.0\" }")
                : null,
        };
        var publisher = new PackagePublishService(p.Git, github.Api(), p.Mounts, (_, _, _) => bare);

        var plan = (await publisher.PlanAsync(folder, null))!;
        Assert.Equal("0.1.0", plan.CurrentVersion);
        Assert.Equal("0.1.0", plan.SuggestedVersion);
        Assert.Null(plan.LastTag);
        Assert.Equal("main", plan.Branch);
        Assert.True(plan.IsGitHub);
        Assert.Equal(1, plan.ChangedFiles);

        var result = await publisher.PublishAsync(plan, new PublishRequest("0.1.0", "- First release"), "token", Tester);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("v0.1.0", result.Tag);
        Assert.Equal("https://github.com/tester/seats/releases/tag/v0.1.0", result.ReleaseUrl);
        Assert.Equal("Release 0.1.0", GitSandbox.Run(folder, "log", "-1", "--format=%s"));
        Assert.Equal("Tester", GitSandbox.Run(folder, "log", "-1", "--format=%an"));
        Assert.Equal("", GitSandbox.Status(folder));
        Assert.Equal(GitSandbox.Run(folder, "rev-parse", "HEAD"), GitSandbox.Run(bare, "rev-parse", "refs/heads/main"));
        Assert.Equal(GitSandbox.Run(folder, "rev-parse", "v0.1.0^{commit}"), GitSandbox.Run(bare, "rev-parse", "v0.1.0^{commit}"));
        Assert.Equal("tag", GitSandbox.Run(bare, "cat-file", "-t", "v0.1.0"));
        Assert.Contains("## [0.1.0] - ", File.ReadAllText(Path.Combine(folder, "CHANGELOG.md")));
        Assert.Contains("class Seat", GitSandbox.Run(bare, "show", "v0.1.0:Runtime/Seat.cs"));
        var release = Assert.Single(github.Calls);
        using (var body = JsonDocument.Parse(release.Body!))
        {
            Assert.Equal("v0.1.0", body.RootElement.GetProperty("tag_name").GetString());
            Assert.Equal("Seats 0.1.0", body.RootElement.GetProperty("name").GetString());
            Assert.Equal("- First release", body.RootElement.GetProperty("body").GetString());
            Assert.False(body.RootElement.GetProperty("prerelease").GetBoolean());
        }

        var next = (await publisher.PlanAsync(folder, null))!;
        Assert.Equal("v0.1.0", next.LastTag);
        Assert.Equal("0.1.1", next.SuggestedVersion);
        var repeat = await publisher.PublishAsync(next, new PublishRequest("0.1.0", ""), "token", Tester);
        Assert.False(repeat.Ok);
        Assert.Contains("already exists", repeat.Message);
    }

    [GitFact]
    public async Task A_failed_push_puts_everything_back()
    {
        using var p = new AuthoringProject();
        var created = await p.Scaffold.CreateAsync(p.Install(), Request());
        var folder = created.Folder!;
        GitSandbox.Run(folder, "remote", "add", "origin", "https://github.com/tester/seats.git");
        var head = GitSandbox.Run(folder, "rev-parse", "HEAD");
        var package = File.ReadAllText(Path.Combine(folder, "package.json"));
        var changelog = File.ReadAllText(Path.Combine(folder, "CHANGELOG.md"));
        File.WriteAllText(Path.Combine(folder, "README.md"), "# Changed\n");
        var github = new FakeGitHub();
        var publisher = new PackagePublishService(p.Git, github.Api(), p.Mounts, (_, _, _) => p.Temp("missing.git"));

        var result = await publisher.PublishAsync((await publisher.PlanAsync(folder, null))!, new PublishRequest("0.2.0", "- Notes"), "token", Tester);

        Assert.False(result.Ok);
        Assert.Contains("nothing was published", result.Message);
        Assert.Equal(head, GitSandbox.Run(folder, "rev-parse", "HEAD"));
        Assert.Equal("", GitSandbox.Run(folder, "tag", "--list"));
        Assert.Equal(package, File.ReadAllText(Path.Combine(folder, "package.json")));
        Assert.Equal(changelog, File.ReadAllText(Path.Combine(folder, "CHANGELOG.md")));
        Assert.Equal(" M README.md", GitSandbox.Status(folder));
        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task Registry_submission_forks_writes_one_file_and_opens_a_pull_request()
    {
        var github = new FakeGitHub
        {
            Respond = (method, path) => (method.Method, path) switch
            {
                ("GET", "repos/BasisVR/BasisPackageManager") => (HttpStatusCode.OK, "{ \"name\": \"BasisPackageManager\", \"full_name\": \"BasisVR/BasisPackageManager\", \"default_branch\": \"main\", \"permissions\": { \"push\": false } }"),
                ("POST", "repos/BasisVR/BasisPackageManager/forks") => (HttpStatusCode.Accepted, "{ \"name\": \"BasisPackageManager\", \"full_name\": \"tester/BasisPackageManager\", \"owner\": { \"login\": \"tester\", \"id\": 42 } }"),
                ("GET", "repos/tester/BasisPackageManager") => (HttpStatusCode.OK, "{ \"name\": \"BasisPackageManager\", \"full_name\": \"tester/BasisPackageManager\" }"),
                ("POST", "repos/tester/BasisPackageManager/merge-upstream") => (HttpStatusCode.OK, "{}"),
                ("GET", "repos/tester/BasisPackageManager/git/ref/heads/main") => (HttpStatusCode.OK, "{ \"ref\": \"refs/heads/main\", \"object\": { \"sha\": \"abc123\" } }"),
                ("POST", "repos/tester/BasisPackageManager/git/refs") => (HttpStatusCode.Created, "{}"),
                ("PUT", "repos/tester/BasisPackageManager/contents/src/BasisPM.Server/seed/packages/com.studio.seats.json") => (HttpStatusCode.Created, "{}"),
                ("GET", var pulls) when pulls.StartsWith("repos/BasisVR/BasisPackageManager/pulls?", StringComparison.Ordinal) => (HttpStatusCode.OK, "[]"),
                ("POST", "repos/BasisVR/BasisPackageManager/pulls") => (HttpStatusCode.Created, "{ \"html_url\": \"https://github.com/BasisVR/BasisPackageManager/pull/7\", \"number\": 7 }"),
                _ => null,
            },
        };
        var publisher = new PackagePublishService(new GitService(), github.Api(), new MountService(new GitService(), new UnityProjectService(), new MountRegistry(Path.GetTempPath())), delay: (_, _) => Task.CompletedTask);
        var package = PackageJsonFile.Parse(PackageScaffoldService.PackageJson(Request()))!;
        var entry = PackagePublishService.EntryFor(package, UpmGitUrl.Parse("https://github.com/tester/seats.git")!, null, "Worlds", new[] { "Seats", "furniture" }, server: true);

        var result = await publisher.SubmitToRegistryAsync(entry, "token", Tester);

        Assert.True(result.Ok, result.Message);
        Assert.Equal("https://github.com/BasisVR/BasisPackageManager/pull/7", result.PullRequestUrl);
        var branch = github.Calls.Single(c => c.Path == "repos/tester/BasisPackageManager/git/refs");
        using (var json = JsonDocument.Parse(branch.Body!))
        {
            Assert.Equal("refs/heads/add-package-com-studio-seats", json.RootElement.GetProperty("ref").GetString());
            Assert.Equal("abc123", json.RootElement.GetProperty("sha").GetString());
        }
        var put = github.Calls.Single(c => c.Method == HttpMethod.Put);
        using (var json = JsonDocument.Parse(put.Body!))
        {
            Assert.Equal("add-package-com-studio-seats", json.RootElement.GetProperty("branch").GetString());
            var file = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("content").GetString()!));
            using var registry = JsonDocument.Parse(file);
            Assert.Equal("com.studio.seats", registry.RootElement.GetProperty("id").GetString());
            Assert.Equal("Seats", registry.RootElement.GetProperty("name").GetString());
            Assert.Equal("https://github.com/tester/seats.git", registry.RootElement.GetProperty("gitUrl").GetString());
            Assert.Equal("https://github.com/tester/seats", registry.RootElement.GetProperty("repoUrl").GetString());
            Assert.Equal("community", registry.RootElement.GetProperty("source").GetString());
            Assert.Equal("Worlds", registry.RootElement.GetProperty("category").GetString());
            Assert.Equal(new[] { "seats", "furniture" }, registry.RootElement.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).ToArray());
            Assert.Equal("2026-09-16", registry.RootElement.GetProperty("basisVersion").GetString());
            Assert.True(registry.RootElement.GetProperty("server").GetBoolean());
        }
        var pull = github.Calls.Single(c => c.Method == HttpMethod.Post && c.Path == "repos/BasisVR/BasisPackageManager/pulls");
        using (var json = JsonDocument.Parse(pull.Body!))
        {
            Assert.Equal("tester:add-package-com-studio-seats", json.RootElement.GetProperty("head").GetString());
            Assert.Equal("main", json.RootElement.GetProperty("base").GetString());
            Assert.Equal("Add package: Seats", json.RootElement.GetProperty("title").GetString());
        }
    }

    [Fact]
    public async Task Registry_submission_refuses_hosts_the_registry_does_not_list()
    {
        var github = new FakeGitHub();
        var publisher = new PackagePublishService(new GitService(), github.Api(), new MountService(new GitService(), new UnityProjectService(), new MountRegistry(Path.GetTempPath())));
        var package = PackageJsonFile.Parse(PackageScaffoldService.PackageJson(Request()))!;
        var entry = PackagePublishService.EntryFor(package, UpmGitUrl.Parse("https://example.org/tester/seats.git")!, null, "Misc", Array.Empty<string>(), false);

        var result = await publisher.SubmitToRegistryAsync(entry, "token", Tester);

        Assert.False(result.Ok);
        Assert.Empty(github.Calls);
    }
}
