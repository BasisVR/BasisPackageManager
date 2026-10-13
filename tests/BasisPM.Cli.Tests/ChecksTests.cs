using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class ChecksTests
{
    private static async Task<CliHarness> ProjectAsync()
    {
        var cli = new CliHarness("{ \"name\": \"Test registry\", \"packages\": { } }");
        Assert.Equal(0, await cli.RunAsync("projects", "add", cli.MakeBasis("Proj")));
        return cli;
    }

    private static void AddPackage(CliHarness cli, string id, string asmdef, params (string Path, string Content)[] files)
    {
        cli.Temp.WriteFile($"Proj/Basis/Packages/{id}/package.json", $"{{ \"name\": \"{id}\", \"version\": \"1.0.0\", \"displayName\": \"{id}\", \"description\": \"A package.\" }}");
        cli.Temp.WriteFile($"Proj/Basis/Packages/{id}/Runtime/{asmdef}.asmdef", $"{{ \"name\": \"{asmdef}\" }}");
        foreach (var (path, content) in files) cli.Temp.WriteFile($"Proj/Basis/Packages/{id}/{path}", content);
    }

    private static IReadOnlyList<string> Codes(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("issues").EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList();

    [Fact]
    public async Task Verify_without_compiling_reports_a_package_that_hides_UnityEngine_Debug()
    {
        using var cli = await ProjectAsync();
        AddPackage(cli, "com.example.bad", "Example.Bad", ("Runtime/Debug.cs", "public class Debug { }\n"));
        cli.Temp.WriteFile("Proj/Basis/Assets/Game.cs", "using UnityEngine;\npublic class Game : MonoBehaviour { void Start() { Debug.Log(\"hi\"); } }\n");

        Assert.Equal(1, await cli.RunAsync("verify", "--no-compile", "--json"));

        Assert.Contains("global-type-shadows", Codes(cli.Stdout));
    }

    [Fact]
    public async Task Verify_without_compiling_passes_a_tidy_project()
    {
        using var cli = await ProjectAsync();
        AddPackage(cli, "com.example.good", "Example.Good", ("Runtime/Thing.cs", "namespace Example.Good { public class Thing { } }\n"));

        Assert.Equal(0, await cli.RunAsync("verify", "com.example.good", "--no-compile"));
        Assert.Contains("Not compiled", cli.All);
    }

    [Fact]
    public async Task Verify_rejects_a_package_that_is_not_in_the_project()
    {
        using var cli = await ProjectAsync();
        AddPackage(cli, "com.example.good", "Example.Good");

        Assert.Equal(2, await cli.RunAsync("verify", "com.example.goood", "--no-compile"));
        Assert.Contains("com.example.good", cli.Stderr);
    }

    [Fact]
    public async Task Verify_saves_results_the_next_run_compares_with()
    {
        using var cli = await ProjectAsync();
        AddPackage(cli, "com.example.good", "Example.Good", ("Runtime/Thing.cs", "namespace Example.Good { public class Thing { } }\n"));
        Assert.Equal(0, await cli.RunAsync("verify", "--no-compile"));
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(cli.StateDirectory, "checks"), "*.json"));

        AddPackage(cli, "com.example.twin", "Example.Good");
        Assert.Equal(1, await cli.RunAsync("verify", "--no-compile"));
        Assert.Contains("(new)", cli.Stdout);
    }

    [Fact]
    public async Task Check_package_flags_a_git_url_dependency()
    {
        using var cli = new CliHarness();
        var folder = cli.Temp.WriteFile("pkg/package.json", "{ \"name\": \"com.example.pkg\", \"version\": \"1.0.0\", \"displayName\": \"Pkg\", \"dependencies\": { \"com.example.other\": \"https://github.com/example/other.git\" } }");

        Assert.Equal(1, await cli.RunAsync("check-package", Path.GetDirectoryName(folder)!, "--json"));

        Assert.Contains("dependency-url", Codes(cli.Stdout));
    }

    [Fact]
    public async Task Check_package_needs_a_package_json()
    {
        using var cli = new CliHarness();
        var folder = cli.Temp.CreateDir("empty");

        Assert.Equal(1, await cli.RunAsync("check-package", folder));
        Assert.Contains("package.json", cli.Stderr);
    }
}
