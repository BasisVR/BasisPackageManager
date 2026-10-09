using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class CompletionTests
{
    private static IReadOnlyList<string> Complete(CliHarness cli, string line)
    {
        var (words, current, _) = ConsoleApplication.SplitForCompletion(line);
        return cli.App.Candidates(words, current, shell: true);
    }

    [Fact]
    public void SplitKnowsWhetherANewWordStarted()
    {
        var (words, current, start) = ConsoleApplication.SplitForCompletion("install-package com.ex");
        Assert.Equal(new[] { "install-package" }, words);
        Assert.Equal("com.ex", current);
        Assert.Equal(16, start);

        (words, current, start) = ConsoleApplication.SplitForCompletion("status ");
        Assert.Equal(new[] { "status" }, words);
        Assert.Equal("", current);
        Assert.Equal(7, start);
    }

    [Fact]
    public void SplitKeepsQuotedWordsTogether()
    {
        var (words, current, start) = ConsoleApplication.SplitForCompletion("use \"My Pro");
        Assert.Equal(new[] { "use" }, words);
        Assert.Equal("My Pro", current);
        Assert.Equal(4, start);
        (words, current, _) = ConsoleApplication.SplitForCompletion("use \"C:\\a b\"\\c");
        Assert.Equal("C:\\a b\\c", current);
    }

    [Fact]
    public void CompletesCommandsFlagsAndVerbs()
    {
        using var cli = new CliHarness();
        Assert.Contains("status", Complete(cli, "st"));
        Assert.DoesNotContain("statsu", Complete(cli, "st"));
        Assert.Contains("--project", Complete(cli, "--pro"));
        Assert.Equal(new[] { "--backup", "--branch" }, Complete(cli, "update-basis --b"));
        Assert.Contains("reconcile", Complete(cli, "basisdev "));
        Assert.Equal(new[] { "mine", "basis", "theirs", "done" }.OrderBy(s => s), Complete(cli, "resolve some/file.cs "));
        Assert.Contains("--json", Complete(cli, "status --j"));
        Assert.DoesNotContain("--json", Complete(cli, "update-basis --j"));
        Assert.Equal(new[] { "bash", "fish", "powershell", "zsh" }, Complete(cli, "completion "));
    }

    [Fact]
    public void CompletesTheValueOfAnOption()
    {
        using var cli = new CliHarness();
        Assert.Equal(new[] { "avatar", "prop", "world" }, Complete(cli, "server-content add https://x --mode "));
        Assert.Contains("android", Complete(cli, "unity install --module an"));
    }

    [Fact]
    public async Task CompletesSavedProjectNames()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Main Clone");
        Assert.Equal(0, await cli.RunAsync("projects", "add", basis, "--name", "Main"));
        Assert.Equal(new[] { "Main" }, Complete(cli, "--project M"));
        Assert.Equal(new[] { "Main" }, Complete(cli, "use "));
    }

    [Fact]
    public async Task CompletesInstalledPackagesFromTheProject()
    {
        using var cli = new CliHarness();
        var basis = cli.MakeBasis("Proj", manifest: "{ \"dependencies\": { \"com.example.tool\": \"https://github.com/example/tool.git\" } }");
        Assert.Equal(0, await cli.RunAsync("projects", "add", basis));
        var candidates = Complete(cli, "remove-package com.");
        Assert.Contains("com.example.tool", candidates);
        Assert.Contains("com.basis.framework", candidates);
    }

    [Fact]
    public void CompletesPathsBelowAFolder()
    {
        using var cli = new CliHarness();
        cli.Temp.CreateDir("tree/sub/alpha");
        cli.Temp.CreateDir("tree/sub/apple");
        cli.Temp.WriteFile("tree/sub/beta.txt", "");
        var partial = cli.Temp.Combine("tree", "sub", "a");
        var (words, current, _) = ConsoleApplication.SplitForCompletion("projects add " + partial);
        var candidates = cli.App.Candidates(words, current, shell: false);
        var separator = Path.DirectorySeparatorChar;
        Assert.Equal(new[] { cli.Temp.Combine("tree", "sub", "alpha") + separator, cli.Temp.Combine("tree", "sub", "apple") + separator }, candidates);
        Assert.Empty(cli.App.Candidates(words, current, shell: true));
    }

    [Fact]
    public void HiddenCommandsAreNotOffered()
    {
        using var cli = new CliHarness();
        Assert.DoesNotContain("__complete", Complete(cli, "_"));
        Assert.DoesNotContain("exit", Complete(cli, "ex"));
    }

    [Fact]
    public async Task CompleteCommandReadsTheShellsLineFromTheEnvironment()
    {
        using var cli = new CliHarness();
        Environment.SetEnvironmentVariable("COMP_LINE", "basispm upd");
        Environment.SetEnvironmentVariable("COMP_POINT", "11");
        try
        {
            Assert.Equal(0, await cli.RunAsync("__complete", "--shell", "fish"));
            var lines = cli.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Contains(lines, l => l.StartsWith("update-all\t", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("update-basis\t", StringComparison.Ordinal));
            Assert.DoesNotContain('\r', cli.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMP_LINE", null);
            Environment.SetEnvironmentVariable("COMP_POINT", null);
        }
    }

    [Fact]
    public async Task CursorPastTheEndStartsANewWord()
    {
        using var cli = new CliHarness();
        Environment.SetEnvironmentVariable("COMP_LINE", "basispm completion");
        Environment.SetEnvironmentVariable("COMP_POINT", "19");
        try
        {
            Assert.Equal(0, await cli.RunAsync("__complete", "--shell", "bash"));
            Assert.Equal("bash\nfish\npowershell\nzsh\n", cli.Stdout);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COMP_LINE", null);
            Environment.SetEnvironmentVariable("COMP_POINT", null);
        }
    }

    [Theory]
    [InlineData("powershell", "Register-ArgumentCompleter")]
    [InlineData("bash", "complete -o default -F _basispm_complete")]
    [InlineData("zsh", "compdef _basispm")]
    [InlineData("fish", "complete -c basispm")]
    public async Task ScriptsCallBackIntoTheCompleter(string shell, string marker)
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("completion", shell));
        Assert.Contains(marker, cli.Stdout);
        Assert.Contains("__complete --shell", cli.Stdout);
        Assert.Contains("COMP_", cli.Stdout);
    }
}
