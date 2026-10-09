using Xunit;

namespace BasisPM.Cli.Tests;

public sealed class CommandTableTests
{
    private static IReadOnlyList<CliCommand> Commands() => new ConsoleApplication().Commands;

    [Fact]
    public void NamesAndAliasesAreUnique()
    {
        var names = Commands().SelectMany(c => c.Names).ToList();
        var duplicates = names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void EveryCommandIsDocumented()
    {
        foreach (var command in Commands())
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Summary), command.Name);
            Assert.True(command.Summary.Length <= 55, $"{command.Name} summary is {command.Summary.Length} characters");
            Assert.NotEmpty(command.Usage);
            Assert.All(command.Usage, u => Assert.StartsWith(command.Name, u));
            Assert.Contains(command.Group, ConsoleApplication.GroupOrder);
        }
    }

    [Fact]
    public void OptionsAreWellFormed()
    {
        foreach (var command in Commands())
        {
            Assert.All(command.Options, o => Assert.Matches("^--[a-z][a-z-]*$", o.Flag));
            Assert.All(command.Options.Where(o => o.Short is not null), o => Assert.Matches("^-[a-zA-Z]$", o.Short!));
            var flags = command.Options.Select(o => o.Flag).Concat(command.Options.Where(o => o.Short is not null).Select(o => o.Short!)).ToList();
            Assert.Equal(flags.Count, flags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
    }

    [Fact]
    public void ExamplesAndSeeAlsoPointAtRealCommands()
    {
        var commands = Commands();
        var names = commands.SelectMany(c => c.Names).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands)
        {
            Assert.All(command.Examples, e => Assert.Contains(ConsoleApplication.Tokenize(e.Command).First(), names));
            Assert.All(command.SeeAlso, s => Assert.Contains(s, names));
            foreach (var example in command.Examples)
            {
                var args = ConsoleApplication.Tokenize(example.Command).ToList();
                var target = commands.First(c => c.Names.Contains(args[0], StringComparer.OrdinalIgnoreCase));
                ConsoleApplication.CheckOptions(target, ConsoleApplication.NormalizeOptions(target, args.Skip(1).ToList()));
            }
        }
    }

    [Fact]
    public void HelpTextHasNoEmDashes()
    {
        foreach (var command in Commands())
        {
            var text = string.Join("\n", new[] { command.Summary, command.Details ?? "" }.Concat(command.Usage).Concat(command.Options.Select(o => o.Help)).Concat(command.Examples.Select(e => e.Text)));
            Assert.DoesNotContain('—', text);
        }
    }

    [Fact]
    public void VerbCommandsCompleteTheirVerbsFirst()
    {
        foreach (var command in Commands().Where(c => c.Verbs is not null))
            Assert.Equal(ArgKind.Verb, command.ArgAt(Array.Empty<string>()));
    }
}
