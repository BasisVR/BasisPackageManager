using System.Text.Json;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class AuthoringTests
{
    [Fact]
    public async Task BadIdsAndMixedVersionFlagsAreUsageErrors()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Proj");
        Assert.Equal(2, await cli.RunAsync("new-package", "Seats", "--project", basis));
        Assert.Contains("isn't a valid package id", cli.Stderr);
        Assert.Equal(2, await cli.RunAsync("new-package", "com.test.seats", "--license", "gpl", "--project", basis));
        Assert.Equal(2, await cli.RunAsync("publish-package", "com.test.seats", "--version", "1.0.0", "--bump", "patch", "--project", basis));
        Assert.False(Directory.Exists(Path.Combine(basis, "Basis", ".basisdev")));
    }

    [Fact]
    public async Task ANewPackageIsRemovedFromEverywhere()
    {
        if (!new GitService().IsAvailable) return;
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Proj");
        Assert.Equal(0, await cli.RunAsync("new-package", "com.test.gone", "--project", basis));

        Assert.Equal(0, await cli.RunAsync("remove-package", "com.test.gone", "--yes", "--project", basis));

        Assert.False(Directory.Exists(Path.Combine(basis, "Basis", ".basisdev", "com.test.gone")));
        Assert.False(File.Exists(Path.Combine(basis, "Basis", ".basisdev", "com.test.gone.json")));
        Assert.DoesNotContain("com.test.gone", cli.ReadManifest("Proj"));
        Assert.Equal(1, await cli.RunAsync("remove-package", "com.test.gone", "--yes", "--project", basis));
        Assert.Contains("isn't installed", cli.Stderr);
    }

    [Fact]
    public async Task NewPackageThenADryRunOfItsFirstRelease()
    {
        if (!new GitService().IsAvailable) return;
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Proj");

        Assert.Equal(0, await cli.RunAsync("new-package", "com.test.seats", "--name", "Seats", "--no-editor", "--server", "--project", basis));
        var folder = Path.Combine(basis, "Basis", ".basisdev", "com.test.seats");
        Assert.True(File.Exists(Path.Combine(folder, "package.json")));
        Assert.True(File.Exists(Path.Combine(folder, "Runtime", "Test.Seats.asmdef")));
        Assert.False(Directory.Exists(Path.Combine(folder, "Editor")));
        Assert.True(File.Exists(Path.Combine(folder, "Server~", "BasisNetworkServer", "SeatsServerPackage.cs")));
        Assert.True(Directory.Exists(Path.Combine(folder, ".git")));
        Assert.Contains("\"com.test.seats\": \"file:../.basisdev/com.test.seats\"", cli.ReadManifest("Proj"));
        Assert.Contains("publish-package com.test.seats", cli.Stderr);

        Assert.Equal(1, await cli.RunAsync("new-package", "com.test.seats", "--project", basis));
        Assert.Contains("already exists", cli.Stderr);

        Assert.Equal(0, await cli.RunAsync("publish-package", "com.test.seats", "--dry-run", "--json", "--project", basis));
        using (var json = JsonDocument.Parse(cli.Stdout))
        {
            Assert.Equal("0.1.0", json.RootElement.GetProperty("current").GetString());
            Assert.Equal("0.1.0", json.RootElement.GetProperty("version").GetString());
            Assert.Equal("v0.1.0", json.RootElement.GetProperty("tag").GetString());
            Assert.Equal("main", json.RootElement.GetProperty("branch").GetString());
            Assert.False(json.RootElement.TryGetProperty("repository", out _));
            Assert.Equal("- Create com.test.seats", json.RootElement.GetProperty("notes").GetString());
            Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), i => i.GetProperty("code").GetString() == "description-missing");
        }

        Assert.Equal(0, await cli.RunAsync("publish-package", "com.test.seats", "--bump", "minor", "--dry-run", "--project", basis));
        Assert.Contains("Publishes 0.2.0 as tag v0.2.0", cli.Stdout);
        Assert.Equal(1, await cli.RunAsync("publish-package", "com.test.seats", "--yes", "--project", basis));
        Assert.Contains("has no remote repository yet", cli.Stderr);
    }
}
