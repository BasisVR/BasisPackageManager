using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class ProjectsTests
{
    [Fact]
    public async Task AddRenameDefaultAndRemoveAProject()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Clone");

        Assert.Equal(0, await cli.RunAsync("projects", "add", basis, "--name", "Main"));
        var settings = await cli.Settings.LoadAsync();
        Assert.Equal(new[] { basis }, settings.Installs);
        Assert.Equal("Main", settings.InstallAliases[basis]);

        Assert.Equal(0, await cli.RunAsync("projects", "--json"));
        using (var json = JsonDocument.Parse(cli.Stdout))
        {
            var project = json.RootElement[0];
            Assert.Equal("Main", project.GetProperty("name").GetString());
            Assert.Equal("6000.7.0b4", project.GetProperty("unityVersion").GetString());
            Assert.True(project.GetProperty("isBasisCheckout").GetBoolean());
        }

        Assert.Equal(0, await cli.RunAsync("projects", "rename", "main", "Main LTS"));
        Assert.Equal("Main LTS", (await cli.Settings.LoadAsync()).InstallAliases[basis]);

        Assert.Equal(0, await cli.RunAsync("projects", "default", "Main LTS"));
        Assert.Equal(basis, DefaultProject(cli));

        Assert.Equal(0, await cli.RunAsync("projects", "remove", "1"));
        settings = await cli.Settings.LoadAsync();
        Assert.Empty(settings.Installs);
        Assert.Empty(settings.InstallAliases);
        Assert.Null(DefaultProject(cli));
        Assert.True(Directory.Exists(basis));
    }

    private static string? DefaultProject(CliHarness cli)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(cli.StateDirectory, "console.json")));
        return json.RootElement.GetProperty("DefaultProject").GetString();
    }

    [Fact]
    public async Task RefusesFoldersThatAreNotBasisCheckouts()
    {
        using var cli = new CliHarness();
        cli.Temp.WriteFile("Game/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.1f1\n");
        cli.Temp.CreateDir("Game/Assets");
        Assert.Equal(1, await cli.RunAsync("projects", "add", cli.Temp.Combine("Game")));
        Assert.Contains("isn't a Basis checkout", cli.Stderr);
        Assert.Empty((await cli.Settings.LoadAsync()).Installs);
    }

    [Fact]
    public async Task DuplicateNamesAreRefused()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("One"), "--name", "Main"));
        Assert.Equal(1, await cli.RunAsync("projects", "add", cli.MakeBasis("Two"), "--name", "main"));
        Assert.Contains("already called", cli.Stderr);
    }

    [Fact]
    public async Task TheOnlySavedProjectIsUsedWithoutAsking()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Solo");
        Assert.Equal(0, await cli.RunAsync("projects", "add", basis));
        Assert.Equal(0, await cli.RunAsync("status", "--json"));
        using var json = JsonDocument.Parse(cli.Stdout);
        Assert.Equal(basis, json.RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task SeveralProjectsNeedAChoiceOrADefault()
    {
        using var cli = new CliHarness();
        var first = cli.MakeBasis("First");
        var second = cli.MakeBasis("Second");
        Assert.Equal(0, await cli.RunAsync("projects", "add", first));
        Assert.Equal(0, await cli.RunAsync("projects", "add", second));

        Assert.Equal(1, await cli.RunAsync("status"));
        Assert.Contains("Which project?", cli.Stderr);
        Assert.Contains("First, Second", cli.Stderr);

        Assert.Equal(0, await cli.RunAsync("--project", "Second", "status", "--json"));
        Assert.Contains("Second", JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());

        Assert.Equal(0, await cli.RunAsync("projects", "default", "First"));
        Assert.Equal(0, await cli.RunAsync("status", "--json"));
        Assert.Equal(first, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task ANameThatIsAlsoAFolderHereAsksWhichOneYouMean()
    {
        using var cli = new CliHarness();
        var saved = cli.MakeBasis("Saved");
        Assert.Equal(0, await cli.RunAsync("projects", "add", saved, "--name", "Basis"));
        cli.MakeBasis("work/Basis", unityVersion: "6000.3.11f1");
        Assert.Equal(1, await cli.RunAsync("--project", "Basis", "status", "--json"));
        Assert.Contains("is both the saved project", cli.Stderr);
        Assert.Equal(0, await cli.RunAsync("--project", "1", "status", "--json"));
        Assert.Equal(saved, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
        Assert.Equal(0, await cli.RunAsync("--project", "./Basis", "status", "--json"));
        Assert.Equal("6000.3.11f1", JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("unityVersion").GetString());
    }

    [Fact]
    public async Task SavedNamesWorkWhenNoFolderHereSharesThem()
    {
        using var cli = new CliHarness();
        var saved = cli.MakeBasis("Saved");
        Assert.Equal(0, await cli.RunAsync("projects", "add", saved, "--name", "Basis"));
        Assert.Equal(0, await cli.RunAsync("--project", "basis", "status", "--json"));
        Assert.Equal(saved, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task AFolderHoldingSeveralProjectsDoesNotPickOne()
    {
        using var cli = new CliHarness();
        foreach (var name in new[] { "A", "B" })
        {
            cli.Temp.WriteFile($"work/{name}/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.7.0b4\n");
            cli.Temp.CreateDir($"work/{name}/Assets");
            cli.Temp.WriteFile($"work/{name}/Packages/com.basis.framework/package.json", "{ \"name\": \"com.basis.framework\" }");
        }
        Assert.Equal(1, await cli.RunAsync("status"));
        Assert.Contains("No Basis project is set up yet", cli.Stderr);
        Directory.Delete(cli.Temp.Combine("work", "B"), true);
        Assert.Equal(0, await cli.RunAsync("status", "--json"));
        Assert.Equal(cli.Temp.Combine("work", "A"), JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("unityProject").GetString());
    }

    [Fact]
    public async Task TheCurrentFolderPicksItsProject()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("A")));
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("B")));
        Environment.CurrentDirectory = cli.Temp.Combine("B", "Basis", "Assets");
        Assert.Equal(0, await cli.RunAsync("status", "--json"));
        Assert.Equal(cli.Temp.Combine("B"), JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task AFolderInsideASavedProjectWithoutGitStillFindsTheProject()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Copy");
        Assert.Equal(0, await cli.RunAsync("projects", "add", basis, "--name", "Copy"));
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("Other")));
        Environment.CurrentDirectory = cli.Temp.Combine("Copy", "Basis");
        Assert.Equal(0, await cli.RunAsync("status", "--json"));
        Assert.Equal(basis, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
        Assert.Equal(0, await cli.RunAsync("--project", cli.Temp.Combine("Copy", "Basis"), "status", "--json"));
        Assert.Equal(basis, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
    }

    [Fact]
    public async Task FindTurnsUpClonesAndCanSaveThem()
    {
        using var cli = new CliHarness();
        var one = cli.MakeBasis("search/one");
        var two = cli.MakeBasis("search/nested/two");
        cli.Temp.CreateDir("search/one/.git");
        cli.Temp.CreateDir("search/nested/two/.git");
        Assert.Equal(0, await cli.RunAsync("projects", "add", one));
        Assert.Equal(0, await cli.RunAsync("projects", "find", cli.Temp.Combine("search"), "--json"));
        var found = JsonDocument.Parse(cli.Stdout).RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("path").GetString()!, p => p.GetProperty("saved").GetBoolean());
        Assert.True(found[one]);
        Assert.False(found[two]);
        Assert.Equal(0, await cli.RunAsync("projects", "find", cli.Temp.Combine("search"), "--add"));
        var settings = await cli.Settings.LoadAsync();
        Assert.Equal(new[] { one, two }, settings.Installs);
        Assert.Equal("Basis two", settings.InstallAliases[two]);
        Assert.False(settings.InstallAliases.ContainsKey(one));
        Assert.Equal(2, await cli.RunAsync("projects", "--add"));
    }

    [Fact]
    public async Task SharedNamesAskForANumberOrPath()
    {
        using var cli = new CliHarness();
        var first = cli.MakeBasis("a/Basis");
        var second = cli.MakeBasis("b/Basis");
        Assert.Equal(0, await cli.RunAsync("projects", "add", first));
        Assert.Equal(0, await cli.RunAsync("projects", "add", second));
        Assert.Equal(1, await cli.RunAsync("--project", "Basis", "status"));
        Assert.Contains("2 projects are called Basis", cli.Stderr);
        Assert.Contains("#2 " + second, cli.Stderr);
        Assert.Equal(0, await cli.RunAsync("--project", "2", "status", "--json"));
        Assert.Equal(second, JsonDocument.Parse(cli.Stdout).RootElement.GetProperty("path").GetString());
        Assert.Equal(0, await cli.RunAsync("projects", "remove", second));
        Assert.Equal(new[] { first }, (await cli.Settings.LoadAsync()).Installs);
    }

    [Fact]
    public async Task UnknownProjectNamesGetSuggestions()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("X"), "--name", "Avatars"));
        Assert.Equal(1, await cli.RunAsync("--project", "Avatar", "status"));
        Assert.Contains("Did you mean 'Avatars'?", cli.Stderr);
    }
}
