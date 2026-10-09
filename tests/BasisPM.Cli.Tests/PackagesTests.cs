using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class PackagesTests
{
    private const string Catalog = """
        {
          "name": "Test registry",
          "packages": {
            "com.example.tool": { "versions": { "1.2.0": { "name": "com.example.tool", "displayName": "Example Tool", "version": "1.2.0", "description": "A tool.",
              "dependencies": { "com.example.dep": "^1.0.0" } } } },
            "com.example.dep": { "versions": { "1.0.0": { "name": "com.example.dep", "displayName": "Example Dependency", "version": "1.0.0" } } },
            "com.basis.framework": { "versions": { "0.0.1": { "name": "com.basis.framework", "displayName": "Basis Framework", "version": "0.0.1", "source": "built-in" } } }
          }
        }
        """;

    private static async Task<CliHarness> ProjectAsync(string manifest = "{ \"dependencies\": { \"com.unity.ugui\": \"2.0.0\" } }")
    {
        var cli = new CliHarness(Catalog);
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("Proj", manifest: manifest)));
        return cli;
    }

    private static Dictionary<string, string> Dependencies(CliHarness cli, string folder = "Proj") =>
        JsonDocument.Parse(cli.ReadManifest(folder)).RootElement.GetProperty("dependencies").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);

    [Fact]
    public async Task InstallAddsThePackageAndItsDependencies()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        var dependencies = Dependencies(cli);
        Assert.Equal("1.2.0", dependencies["com.example.tool"]);
        Assert.Equal("1.0.0", dependencies["com.example.dep"]);
        Assert.Equal("2.0.0", dependencies["com.unity.ugui"]);
        Assert.Contains("Installed Example Tool", cli.Stdout);
    }

    [Fact]
    public async Task PackagesThatShipWithBasisAreNeverInstalledOverOrRemoved()
    {
        using var cli = await ProjectAsync();
        var before = cli.ReadManifest("Proj");
        Assert.Equal(0, await cli.RunAsync("install-package", "com.basis.framework"));
        Assert.Contains("ships with Basis", cli.Stdout);
        Assert.Equal(1, await cli.RunAsync("remove-package", "com.basis.framework", "--yes"));
        Assert.Contains("ships with Basis", cli.Stderr);
        Assert.Equal(before, cli.ReadManifest("Proj"));
        Assert.True(File.Exists(cli.Temp.Combine("Proj", "Basis", "Packages", "com.basis.framework", "package.json")));
    }

    [Fact]
    public async Task WithoutGitHistoryManifestLinesNeedForceToRemove()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        Assert.Equal(1, await cli.RunAsync("remove-package", "com.example.tool"));
        Assert.Contains("--force", cli.Stderr);
        Assert.True(Dependencies(cli).ContainsKey("com.example.tool"));
    }

    [Fact]
    public async Task IdCasingCannotSlipPastTheBasisGuard()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(1, await cli.RunAsync("remove-package", "COM.BASIS.FRAMEWORK", "--force", "--yes"));
        Assert.Contains("ships with Basis", cli.Stderr);
        Assert.True(File.Exists(cli.Temp.Combine("Proj", "Basis", "Packages", "com.basis.framework", "package.json")));
    }

    [Fact]
    public async Task ABadVersionChangesNothing()
    {
        using var cli = await ProjectAsync();
        var before = cli.ReadManifest("Proj");
        Assert.Equal(1, await cli.RunAsync("install-package", "com.example.tool", "--version", "v2.0.0"));
        Assert.Contains("no git repository", cli.Stderr);
        Assert.Equal(before, cli.ReadManifest("Proj"));
    }

    [Fact]
    public async Task RemoveTakesOutOnlyThatPackage()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        Assert.Equal(0, await cli.RunAsync("remove-package", "com.example.tool", "--force"));
        var dependencies = Dependencies(cli);
        Assert.False(dependencies.ContainsKey("com.example.tool"));
        Assert.True(dependencies.ContainsKey("com.example.dep"));
        Assert.Equal(1, await cli.RunAsync("remove-package", "com.example.dpe", "--force"));
        Assert.Contains("Did you mean 'com.example.dep'?", cli.Stderr);
    }

    [Fact]
    public async Task ListsWhatIsInstalledAndWhatShipsWithBasis()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        Assert.Equal(0, await cli.RunAsync("list-packages", "--installed", "--json"));
        var installed = JsonDocument.Parse(cli.Stdout).RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains("com.example.tool", installed);
        Assert.DoesNotContain("com.basis.framework", installed);
        Assert.Equal(0, await cli.RunAsync("list-packages", "--built-in", "--json"));
        Assert.Contains("com.basis.framework", JsonDocument.Parse(cli.Stdout).RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task InfoShowsTheProjectState()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        Assert.Equal(0, await cli.RunAsync("info", "com.example.tool", "--json"));
        var json = JsonDocument.Parse(cli.Stdout).RootElement;
        Assert.Equal("Example Tool", json.GetProperty("name").GetString());
        Assert.Equal("yes, manifest.json has 1.2.0", json.GetProperty("project").GetProperty("state").GetString());
        Assert.Equal(1, await cli.RunAsync("info", "com.example.tol"));
        Assert.Contains("Did you mean 'com.example.tool'?", cli.Stderr);
    }

    [Fact]
    public async Task UpdatePackagesSeesNothingNewer()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        var before = cli.ReadManifest("Proj");
        Assert.Equal(0, await cli.RunAsync("update-packages", "com.example.tool", "--dry-run"));
        Assert.Contains("newest release", cli.Stdout);
        Assert.Equal(0, await cli.RunAsync("update-packages", "--dry-run"));
        Assert.Contains("only packages installed as working copies are checked", cli.Stderr);
        Assert.Equal(before, cli.ReadManifest("Proj"));
    }

    [Fact]
    public async Task APinnedVersionMovesToTheNewestOne()
    {
        const string catalog = """
            { "packages": { "com.example.tool": { "versions": {
              "1.2.0": { "name": "com.example.tool", "displayName": "Example Tool", "version": "1.2.0" },
              "1.3.0": { "name": "com.example.tool", "displayName": "Example Tool", "version": "1.3.0" } } } } }
            """;
        using var cli = new CliHarness(catalog);
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("Proj", manifest: "{ \"dependencies\": { \"com.example.tool\": \"1.2.0\" } }")));
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        Assert.Equal("1.3.0", Dependencies(cli)["com.example.tool"]);
    }

    [Fact]
    public async Task ExportedListsInstallIntoAnotherProject()
    {
        using var cli = await ProjectAsync();
        Assert.Equal(0, await cli.RunAsync("install-package", "com.example.tool"));
        var file = cli.Temp.Combine("list.json");
        Assert.Equal(0, await cli.RunAsync("export-package-list", file, "--name", "My Set"));
        using (var list = JsonDocument.Parse(File.ReadAllText(file)))
        {
            Assert.Equal("my-set", list.RootElement.GetProperty("id").GetString());
            var ids = list.RootElement.GetProperty("packages").EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
            Assert.Contains("com.example.tool", ids);
            Assert.DoesNotContain("com.unity.ugui", ids);
            Assert.DoesNotContain("com.basis.framework", ids);
        }
        Assert.Equal(1, await cli.RunAsync("export-package-list", file));
        Assert.Contains("already exists", cli.Stderr);

        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("Other")));
        Assert.Equal(0, await cli.RunAsync("--project", "Other", "install-package-list", file));
        Assert.Equal("1.2.0", Dependencies(cli, "Other")["com.example.tool"]);
    }
}
