using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class SafetyTests
{
    private static string RiskyPackage(CliHarness cli)
    {
        cli.Temp.WriteFile("risky/package.json", """{ "name": "com.example.risky", "version": "0.1.0" }""");
        cli.Temp.WriteFile("risky/Editor/Boot.cs", "using UnityEditor;\n[InitializeOnLoad]\nstatic class Boot { static Boot() { System.Diagnostics.Process.Start(\"calc\"); } }\n");
        return cli.Temp.Combine("risky");
    }

    [Fact]
    public async Task ScanListsWhatAPackageCanDo()
    {
        using var cli = new CliHarness();

        Assert.Equal(0, await cli.RunAsync("scan", RiskyPackage(cli)));

        Assert.Contains("Starts other programs or commands", cli.Stdout);
        Assert.Contains("Editor/Boot.cs:3", cli.Stdout);
        Assert.Contains("Runs code by itself", cli.Stdout);
        Assert.Contains("0.1.0", cli.Stdout);
    }

    [Fact]
    public async Task ScanJsonReportsEveryFinding()
    {
        using var cli = new CliHarness();

        Assert.Equal(0, await cli.RunAsync("scan", RiskyPackage(cli), "--json"));

        using var json = JsonDocument.Parse(cli.Stdout);
        Assert.True(json.RootElement.GetProperty("highRisk").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("scripts").GetInt32());
        var findings = json.RootElement.GetProperty("findings").EnumerateArray().ToList();
        Assert.Contains(findings, f => f.GetProperty("capability").GetString() == "StartsPrograms" && f.GetProperty("line").GetInt32() == 3);
    }

    [Fact]
    public async Task StrictScanFailsOnlyWhenSomethingHighRiskIsFound()
    {
        using var cli = new CliHarness();
        cli.Temp.WriteFile("clean/package.json", """{ "name": "com.example.clean", "version": "1.0.0" }""");
        cli.Temp.WriteFile("clean/Runtime/Spin.cs", "class Spin : UnityEngine.MonoBehaviour { }\n");

        Assert.Equal(0, await cli.RunAsync("scan", cli.Temp.Combine("clean"), "--strict"));
        Assert.Contains("Nothing stood out", cli.Stdout);
        Assert.Equal(1, await cli.RunAsync("scan", RiskyPackage(cli), "--strict"));
    }

    [Fact]
    public async Task ScanRefusesUnsafeGitUrls()
    {
        using var cli = new CliHarness();

        Assert.Equal(1, await cli.RunAsync("scan", "ext::sh -c touch% /tmp/pwned"));
        Assert.Equal(1, await cli.RunAsync("scan", "https://github.com/example/tool.git#--upload-pack=evil"));
    }
}
