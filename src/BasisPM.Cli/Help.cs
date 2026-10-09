namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private string Prefix => _interactive ? "" : "basispm ";

    private IEnumerable<CliCommand> VisibleCommands => _commands.Where(c => !c.Hidden && (_interactive || !c.Interactive));

    private CliCommand? FindCommand(string name) => _byName.GetValueOrDefault(name.ToLowerInvariant());

    private Task HelpAsync(List<string> values)
    {
        Expect(values, 0, 1, "a command name");
        if (values.Count == 0) WriteHelp();
        else WriteCommandHelp(FindCommand(values[0]) ?? throw new UsageException(UnknownCommandMessage(values[0])));
        return Task.CompletedTask;
    }

    private string UnknownCommandMessage(string name)
    {
        var suggestions = Suggest(name, VisibleCommands.SelectMany(c => c.Names));
        return $"Unknown command '{name}'." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" Run '{Prefix}help' to see every command.");
    }

    private void WriteHelp()
    {
        Out.Say(new Span("Basis Package Manager console", Tone.Accent), new Span("  " + VersionText, Tone.Dim));
        Out.Say();
        if (_interactive) Out.Say("Type a command and press Enter. Tab completes commands, options and names; Up and Down walk through history.");
        else
        {
            Out.Say("Usage: basispm [--project <name|path>] <command> [options]");
            Out.Say("       basispm                 Start the interactive console");
        }
        var width = VisibleCommands.Max(c => c.Name.Length) + 2;
        foreach (var group in GroupOrder)
        {
            var commands = VisibleCommands.Where(c => c.Group == group).ToList();
            if (commands.Count == 0) continue;
            Out.Say();
            Out.Heading(group);
            foreach (var command in commands) Definition(command.Name, command.Summary, width);
        }
        Out.Say();
        Out.Heading("Global options");
        var options = new[]
        {
            ("--project <name|path>", "Project to work on. Without it: the project the current folder is in, your default project ('projects default'), or your only one."),
            ("--json", "Print machine-readable JSON (commands that list or show things)"),
            ("--no-color", "Turn colors off (NO_COLOR=1 does the same)"),
            ("-h, --help", "Show a command's options and examples"),
            ("--version", "Show the version"),
        };
        foreach (var (name, text) in options) Definition(name, text, 24);
        Out.Say();
        Out.Note($"Run '{Prefix}help <command>' for its options and examples, for example '{Prefix}help update-basis'.");
        if (!_interactive) Out.Note("Exit codes: 0 done, 1 failed, 2 wrong usage, 130 cancelled. BASISPM_PROJECT sets the default project.");
    }

    private void WriteCommandHelp(CliCommand command)
    {
        Out.Say(new Span(command.Name, Tone.Accent), new Span(": " + command.Summary));
        Out.Say();
        Out.Heading("Usage");
        foreach (var usage in command.Usage.DefaultIfEmpty(command.Name)) Out.Say("  " + Prefix + usage);
        if (command.Details is { Length: > 0 } details)
        {
            Out.Say();
            foreach (var line in Out.Wrap(details, Math.Max(40, Out.Width - 4))) Out.Say("  " + line);
        }
        var options = command.Json ? command.Options.Append(new CliOption("--json", "Print the result as JSON")).ToArray() : command.Options;
        if (options.Length > 0)
        {
            Out.Say();
            Out.Heading("Options");
            var width = options.Max(o => o.Display.Length) + 2;
            foreach (var option in options) Definition(option.Display, option.Help, width);
        }
        if (command.Examples.Length > 0)
        {
            Out.Say();
            Out.Heading("Examples");
            foreach (var example in command.Examples)
            {
                Out.Say("  " + Prefix + example.Command);
                Out.Note("      " + example.Text);
            }
        }
        if (command.Aliases.Length > 0 || command.SeeAlso.Length > 0) Out.Say();
        if (command.Aliases.Length > 0) Out.Note("Aliases: " + string.Join(", ", command.Aliases));
        if (command.SeeAlso.Length > 0) Out.Note("See also: " + string.Join(", ", command.SeeAlso));
    }

    private static void Definition(string term, string text, int width)
    {
        var lines = Out.Wrap(text, Math.Max(30, Out.Width - width - 3)).ToList();
        for (var i = 0; i < lines.Count; i++) Out.Say("  " + (i == 0 ? term : "").PadRight(width) + lines[i]);
    }

    private string UsageHint(CliCommand command)
    {
        var lines = command.Usage.DefaultIfEmpty(command.Name).Select((u, i) => (i == 0 ? "Usage: " : "       ") + Prefix + u);
        return string.Join(Environment.NewLine, lines.Append($"Run '{Prefix}help {command.Name}' for details."));
    }

    internal static IReadOnlyList<string> Suggest(string input, IEnumerable<string> candidates, int max = 3)
    {
        var word = input.Trim().ToLowerInvariant();
        var names = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (word.Length == 0) return Array.Empty<string>();
        var prefix = names.Where(n => n.StartsWith(word, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).ToList();
        if (word.Length >= 2 && prefix.Count > 0) return prefix.Take(max).ToList();
        var limit = word.Length <= 4 ? 1 : word.Length <= 12 ? 2 : 3;
        var close = names.Select(n => (Name: n, Distance: Distance(word, n.ToLowerInvariant()))).Where(x => x.Distance <= limit).ToList();
        if (close.Count > 0)
        {
            var best = close.Min(x => x.Distance);
            return close.Where(x => x.Distance == best).OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name).Take(max).ToList();
        }
        return word.Length >= 4 ? names.Where(n => n.Contains(word, StringComparison.OrdinalIgnoreCase)).OrderBy(n => n.Length).Take(max).ToList() : Array.Empty<string>();
    }

    internal static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    private static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Join("", list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1];
    }

    internal static List<string> NormalizeOptions(CliCommand command, IReadOnlyList<string> values)
    {
        var result = new List<string>();
        foreach (var value in values)
        {
            var equals = value.StartsWith("--", StringComparison.Ordinal) ? value.IndexOf('=') : -1;
            if (equals > 2 && command.FindOption(value[..equals]) is { TakesValue: true } option)
            {
                result.Add(option.Flag);
                result.Add(value[(equals + 1)..]);
            }
            else result.Add(value);
        }
        return result;
    }

    internal static void CheckOptions(CliCommand command, IReadOnlyList<string> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            var token = values[i];
            if (token.Length < 2 || token[0] != '-' || char.IsDigit(token[1])) continue;
            var option = command.FindOption(token);
            if (option is null)
            {
                var flags = command.Options.Select(o => o.Flag).Concat(command.Options.Where(o => o.Short is not null).Select(o => o.Short!)).ToList();
                var match = Suggest(token.TrimStart('-'), flags.Select(f => f.TrimStart('-'))).FirstOrDefault();
                var suggestion = match is null ? null : flags.First(f => f.TrimStart('-').Equals(match, StringComparison.OrdinalIgnoreCase));
                throw new UsageException($"'{command.Name}' has no option '{token}'." + (suggestion is null ? "" : $" Did you mean '{suggestion}'?"));
            }
            if (option.TakesValue)
            {
                if (i + 1 >= values.Count) throw new UsageException($"{option.Flag} needs a value: {option.Display}.");
                i++;
            }
        }
    }
}
