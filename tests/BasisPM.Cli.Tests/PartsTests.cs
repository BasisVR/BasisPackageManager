using System.Diagnostics;
using System.Text.Json;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class PartsTests
{
    [Fact]
    public async Task UnknownPartsAndVerbsAreUsageErrors()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("parts", "remov", "server"));
        Assert.Contains("Did you mean 'remove'?", cli.Stderr);
        Assert.Equal(2, await cli.RunAsync("parts", "remove", "assets"));
        Assert.Contains("Parts: server, images", cli.Stderr);
        Assert.Equal(2, await cli.RunAsync("parts", "add"));
        Assert.Equal(2, await cli.RunAsync("clone-basis", cli.Temp.Combine("New"), "--without", "docs"));
        Assert.False(Directory.Exists(cli.Temp.Combine("New")));
    }

    [Fact]
    public async Task RemoveAndAddAPartOfAProject()
    {
        if (!new GitService().IsAvailable) return;
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Clone");
        cli.Temp.WriteFile("Clone/.gitattributes", "* -text\n");
        cli.Temp.WriteFile("Clone/Basis Server/.gitignore", "bin/\n");
        cli.Temp.WriteFile("Clone/Basis Server/BasisServerConsole/Program.cs", "server\n");
        cli.Temp.WriteFile("Clone/Basis/Images/Banner.png", "banner\n");
        Git(basis, "init", "-q", "-b", "developer");
        Git(basis, "add", "-A");
        Git(basis, "commit", "-q", "-m", "basis");
        cli.Temp.WriteFile("Clone/Basis Server/BasisServerConsole/bin/server.dll", "built\n");

        Assert.Equal(0, await cli.RunAsync("parts", "--json", "--project", basis));
        using (var json = JsonDocument.Parse(cli.Stdout))
        {
            var server = json.RootElement.GetProperty("parts")[0];
            Assert.Equal("server", server.GetProperty("id").GetString());
            Assert.True(server.GetProperty("included").GetBoolean());
            Assert.Equal(1, server.GetProperty("ignoredFiles").GetInt32());
        }

        Assert.Equal(0, await cli.RunAsync("parts", "remove", "server", "--yes", "--project", basis));
        Assert.Contains("Left Basis Server out of", cli.Stdout);
        Assert.False(Directory.Exists(Path.Combine(basis, "Basis Server")));
        Assert.True(File.Exists(Path.Combine(basis, "Basis", "Images", "Banner.png")));

        Assert.Equal(0, await cli.RunAsync("status", "--json", "--project", basis));
        using (var json = JsonDocument.Parse(cli.Stdout))
            Assert.Equal("server", json.RootElement.GetProperty("leftOut")[0].GetString());

        Assert.Equal(0, await cli.RunAsync("parts", "add", "server", "--project", basis));
        Assert.Contains("Added Basis Server back", cli.Stdout);
        Assert.Equal("server\n", File.ReadAllText(Path.Combine(basis, "Basis Server", "BasisServerConsole", "Program.cs")));
        Assert.Equal("", Git(basis, "status", "--porcelain"));
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(new GitService().FindGit()!) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "-c", "core.autocrlf=false" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        return stdout.Result.TrimEnd();
    }
}
