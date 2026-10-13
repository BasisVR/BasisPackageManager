using System.Text.Json;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class StorageTests
{
    [Fact]
    public async Task StorageSplitsTheProjectIntoAreas()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Clone");
        cli.Temp.WriteFile("Clone/Basis/Library/ArtifactDB", new string('l', 4096));
        cli.Temp.WriteFile("Clone/Basis/Library/PackageCache/com.unity.x@1/file.bin", new string('p', 1024));
        cli.Temp.WriteFile("Clone/Basis/Temp/x.bin", "temp");
        cli.Temp.WriteFile("Clone/Basis/.basisdev/com.test.loose/package.json", "{}");

        Assert.Equal(0, await cli.RunAsync("storage", "--json", "--project", basis));

        using var json = JsonDocument.Parse(cli.Stdout);
        var areas = json.RootElement.GetProperty("areas").EnumerateArray().ToDictionary(a => a.GetProperty("area").GetString()!, a => a.GetProperty("bytes").GetInt64());
        Assert.Equal(5120, areas["library"]);
        Assert.Equal(1024, areas["packageCache"]);
        Assert.Equal(4, areas["temp"]);
        var clone = Assert.Single(json.RootElement.GetProperty("clones").EnumerateArray());
        Assert.Equal("com.test.loose", clone.GetProperty("id").GetString());
        var cache = json.RootElement.GetProperty("cache");
        Assert.StartsWith(cli.StateDirectory, cache.GetProperty("path").GetString());
        Assert.True(cache.GetProperty("enabled").GetBoolean());

        Assert.Equal(0, await cli.RunAsync("storage", "--project", basis));
        Assert.Contains("Library", cli.Stdout);
        Assert.Contains("Download cache:", cli.Stdout);
    }

    [Fact]
    public async Task CleanAsksFirstThenDeletesTheFolder()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Clone");
        cli.Temp.WriteFile("Clone/Basis/Library/ArtifactDB", new string('l', 4096));
        cli.Temp.WriteFile("Clone/Basis/Temp/x.bin", "temp");
        cli.Temp.WriteFile("Clone/Basis/Logs/import.log", "log");

        Assert.Equal(2, await cli.RunAsync("clean", "librry", "--project", basis));
        Assert.Contains("Did you mean 'library'?", cli.Stderr);
        Assert.Equal(1, await cli.RunAsync("clean", "temp", "--project", basis));
        Assert.Contains("--yes", cli.Stderr);
        Assert.True(File.Exists(cli.Temp.Combine("Clone/Basis/Temp/x.bin")));

        Assert.Equal(0, await cli.RunAsync("clean", "temp", "--yes", "--project", basis));
        Assert.False(Directory.Exists(cli.Temp.Combine("Clone/Basis/Temp")));
        Assert.Equal(0, await cli.RunAsync("clean", "logs", "--yes", "--project", basis));
        Assert.False(File.Exists(cli.Temp.Combine("Clone/Basis/Logs/import.log")));
        Assert.Equal(0, await cli.RunAsync("clean", "library", "--yes", "--project", basis));
        Assert.Contains("Cleaned Library and freed 4 KB.", cli.Stdout);
        Assert.False(Directory.Exists(cli.Temp.Combine("Clone/Basis/Library")));
        Assert.Equal(0, await cli.RunAsync("clean", "library", "--project", basis));
        Assert.Contains("Library is already empty.", cli.Stdout);
        Assert.Equal(0, await cli.RunAsync("clean", "clones", "--project", basis));
        Assert.Contains("There are no unused clones to remove.", cli.Stdout);
    }

    [Fact]
    public async Task CleanCacheEmptiesTheDownloadCache()
    {
        using var cli = new CliHarness();
        var mirror = Path.Combine(cli.StateDirectory, "git-cache", "github.com", "test", "repo.git");
        Directory.CreateDirectory(Path.Combine(mirror, "objects", "pack"));
        Directory.CreateDirectory(Path.Combine(mirror, "refs"));
        File.WriteAllText(Path.Combine(mirror, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(mirror, "objects", "pack", "pack-1.pack"), new string('x', 2048));

        Assert.Equal(0, await cli.RunAsync("clean", "cache", "--yes"));

        Assert.Contains("Freed 2 KB.", cli.Stdout);
        Assert.False(Directory.Exists(mirror));
        Assert.Equal(0, await cli.RunAsync("clean", "cache"));
        Assert.Contains("already empty", cli.Stdout);
    }

    [Fact]
    public async Task LicensesListPackagesAndWriteNotices()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Clone");
        cli.Temp.WriteFile("Clone/Basis/Packages/com.test.mit/package.json", "{ \"name\": \"com.test.mit\", \"displayName\": \"Mit Tool\", \"version\": \"1.0.0\", \"license\": \"MIT\" }");
        cli.Temp.WriteFile("Clone/Basis/Packages/com.test.mit/LICENSE.md", "MIT License\nPermission is hereby granted, free of charge, to any person.\n");

        Assert.Equal(0, await cli.RunAsync("licenses", "--json", "--project", basis));
        using (var json = JsonDocument.Parse(cli.Stdout))
        {
            var packages = json.RootElement.GetProperty("packages").EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
            Assert.Equal("MIT", packages["com.test.mit"].GetProperty("license").GetString());
            Assert.Equal("missing", packages["com.basis.framework"].GetProperty("concern").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("missing").GetInt32());
        }

        var notices = cli.Temp.Combine("out", "NOTICES.md");
        Assert.Equal(0, await cli.RunAsync("licenses", "--output", notices, "--project", basis));
        Assert.Contains("no license", cli.Stderr);
        Assert.Contains("Saved third-party notices", cli.Stdout);
        var text = File.ReadAllText(notices);
        Assert.StartsWith("# Third-party notices", text);
        Assert.Contains("Permission is hereby granted", text);
    }

    [Fact]
    public async Task CloneCacheSettingRoundTrips()
    {
        using var cli = new CliHarness();

        Assert.Equal(0, await cli.RunAsync("config", "set", "clone-cache", "false"));
        Assert.False((await cli.Settings.LoadAsync()).UseCloneCache);
        Assert.Equal(0, await cli.RunAsync("config", "get", "clone-cache"));
        Assert.Equal("false", cli.Stdout.Trim());
        Assert.Equal(0, await cli.RunAsync("config", "unset", "clone-cache"));
        Assert.True((await cli.Settings.LoadAsync()).UseCloneCache);
    }
}
