using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class ConfigTests
{
    [Fact]
    public async Task SetGetAndUnsetASetting()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("config", "set", "catalog-url", "https://example.org/catalog.json"));
        Assert.Equal("https://example.org/catalog.json", (await cli.Settings.LoadAsync()).CatalogUrl);
        Assert.Equal(0, await cli.RunAsync("config", "get", "catalog-url"));
        Assert.Equal("https://example.org/catalog.json", cli.Stdout.Trim());
        Assert.Equal(0, await cli.RunAsync("config", "unset", "catalog-url"));
        Assert.Equal("", (await cli.Settings.LoadAsync()).CatalogUrl);
    }

    [Fact]
    public async Task BadValuesAreUsageErrors()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("config", "set", "catalog-url", "not a url"));
        Assert.Equal(2, await cli.RunAsync("config", "set", "auto-check-basis-updates", "maybe"));
        Assert.Equal(2, await cli.RunAsync("config", "set", "unity-hub-path", cli.Temp.Combine("nothing-here.exe")));
        Assert.Equal(2, await cli.RunAsync("config", "get", "catalog"));
        Assert.Contains("Did you mean 'catalog-url'?", cli.Stderr);
    }

    [Fact]
    public async Task BooleansAndListsRoundTrip()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("config", "set", "auto-check-basis-updates", "off"));
        Assert.False((await cli.Settings.LoadAsync()).AutoCheckBasisUpdates);
        Assert.Equal(0, await cli.RunAsync("config", "unset", "auto-check-basis-updates"));
        Assert.True((await cli.Settings.LoadAsync()).AutoCheckBasisUpdates);
        Assert.Equal(0, await cli.RunAsync("config", "set", "extra-catalogs", "https://a.example/c.json, https://b.example/c.json"));
        Assert.Equal(new[] { "https://a.example/c.json", "https://b.example/c.json" }, (await cli.Settings.LoadAsync()).ExtraCatalogUrls);
    }

    [Fact]
    public async Task ListsEveryKeyAsJson()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("config", "--json"));
        using var json = JsonDocument.Parse(cli.Stdout);
        foreach (var key in ConsoleApplication.SettingKeys) Assert.True(json.RootElement.TryGetProperty(key.Key, out _), key.Key);
    }

    [Fact]
    public async Task ConfigPathPointsAtTheSettingsFile()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("config", "path"));
        Assert.Equal(cli.Settings.SettingsPath, cli.Stdout.Trim());
    }
}
