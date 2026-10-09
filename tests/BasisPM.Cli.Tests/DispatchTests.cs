using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class DispatchTests
{
    [Fact]
    public async Task HelpListsEveryVisibleCommand()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("help"));
        foreach (var command in cli.App.Commands.Where(c => !c.Hidden && !c.Interactive)) Assert.Contains(command.Name, cli.Stdout);
        Assert.DoesNotContain("__complete", cli.Stdout);
    }

    [Theory]
    [InlineData("help", "update")]
    [InlineData("update-basis", "--help")]
    [InlineData("update", "-h")]
    [InlineData("--help", "pull")]
    public async Task CommandHelpWorksEveryWay(string first, string second)
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync(first, second));
        Assert.Contains("update-basis: ", cli.Stdout);
        Assert.Contains("--branch <name>", cli.Stdout);
        Assert.Contains("Aliases: update, pull", cli.Stdout);
    }

    [Fact]
    public async Task UnknownCommandSuggestsTheClosestOne()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("statsu"));
        Assert.Contains("Did you mean 'status'?", cli.Stderr);
    }

    [Fact]
    public async Task UnknownOptionIsAUsageErrorWithASuggestion()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("basisdev", "--forse"));
        Assert.Contains("Did you mean '--force'?", cli.Stderr);
        Assert.Contains("Usage: basispm basisdev", cli.Stderr);
    }

    [Fact]
    public async Task MissingArgumentsAreUsageErrors()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("resolve"));
        Assert.Contains("Missing", cli.Stderr);
        Assert.Equal(2, await cli.RunAsync("install-package", "a", "b", "--version", "1.0.0"));
        Assert.Contains("--version works with one package", cli.Stderr);
    }

    [Fact]
    public async Task JsonIsRefusedWhereItIsNotSupported()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("update-basis", "--json"));
        Assert.Contains("has no JSON output", cli.Stderr);
    }

    [Fact]
    public async Task VersionFlagPrintsOneLine()
    {
        using var cli = new CliHarness();
        Assert.Equal(0, await cli.RunAsync("--version"));
        Assert.Matches(@"^basispm \S+\r?\n$", cli.Stdout);
    }

    [Fact]
    public async Task CommandsNeedingAProjectExplainHowToPickOne()
    {
        using var cli = new CliHarness();
        Assert.Equal(1, await cli.RunAsync("status"));
        Assert.Contains("No Basis project is set up yet", cli.Stderr);
    }

    [Fact]
    public async Task OptionValuesCanUseEqualsSigns()
    {
        using var cli = new CliHarness();
        Assert.Equal(1, await cli.RunAsync("--project=nowhere-at-all", "status"));
        Assert.Contains("isn't a saved project or a folder", cli.Stderr);
    }

    [Theory]
    [InlineData("statsu", "status")]
    [InlineData("chekc-updates", "check-updates")]
    [InlineData("instal", "install")]
    [InlineData("servr-build", "server-build")]
    public void SuggestionsFindTypos(string typo, string expected)
    {
        var names = new ConsoleApplication().Commands.SelectMany(c => c.Names);
        Assert.Contains(expected, ConsoleApplication.Suggest(typo, names));
    }

    [Fact]
    public void SuggestionsStaySilentForNonsense()
    {
        var names = new ConsoleApplication().Commands.SelectMany(c => c.Names);
        Assert.Empty(ConsoleApplication.Suggest("qqqq", names));
    }

    [Theory]
    [InlineData("status", "status", 0)]
    [InlineData("status", "stauts", 1)]
    [InlineData("abc", "abcd", 1)]
    [InlineData("kitten", "sitting", 3)]
    public void DistanceCountsSwapsAsOneEdit(string a, string b, int expected) => Assert.Equal(expected, ConsoleApplication.Distance(a, b));

    [Fact]
    public async Task DoubleDashIsNotAnOption()
    {
        using var cli = new CliHarness();
        Assert.Equal(2, await cli.RunAsync("status", "--"));
        Assert.Contains("has no option '--'", cli.Stderr);
    }

    [Fact]
    public void TokenizerHandlesQuotedOptionValues()
    {
        Assert.Equal(new[] { "contribute", "--title=Fix seat height" }, ConsoleApplication.Tokenize("contribute --title=\"Fix seat height\""));
        Assert.Equal(new[] { "use", "C:\\Users\\O'Brien\\Basis" }, ConsoleApplication.Tokenize("use C:\\Users\\O'Brien\\Basis"));
    }

    [Fact]
    public void TokenizerHandlesQuotes()
    {
        Assert.Equal(new[] { "use", "C:\\My Projects\\Basis" }, ConsoleApplication.Tokenize("use \"C:\\My Projects\\Basis\""));
        Assert.Equal(new[] { "contribute", "--title", "Say \"hi\"" }, ConsoleApplication.Tokenize("contribute --title \"Say \\\"hi\\\"\""));
        Assert.Equal(new[] { "a", "b c" }, ConsoleApplication.Tokenize("a 'b c'"));
        Assert.Throws<FormatException>(() => ConsoleApplication.Tokenize("use \"unfinished").ToList());
    }

    [Fact]
    public void ServerAddressesParse()
    {
        Assert.Equal(("127.0.0.1", (ushort)4296), ConsoleApplication.SplitAddress(null, 4296));
        Assert.Equal(("example.org", (ushort)4296), ConsoleApplication.SplitAddress("example.org", 4296));
        Assert.Equal(("example.org", (ushort)5000), ConsoleApplication.SplitAddress("example.org:5000", 4296));
        Assert.Equal(("::1", (ushort)7000), ConsoleApplication.SplitAddress("[::1]:7000", 4296));
        Assert.Equal(("::1", (ushort)4296), ConsoleApplication.SplitAddress("::1", 4296));
        Assert.Equal(("127.0.0.1", (ushort)9000), ConsoleApplication.SplitAddress(":9000", 4296));
        Assert.Throws<UsageException>(() => ConsoleApplication.SplitAddress("example.org:http", 4296));
    }
}
