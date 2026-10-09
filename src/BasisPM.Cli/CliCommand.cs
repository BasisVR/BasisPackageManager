namespace BasisPM.Cli;

internal enum ArgKind { None, Path, Verb, Project, Package, InstalledPackage, CatalogPackage, ProjectBranch, BasisBranch, ConflictPath, ConflictChoice, ServerPackage, ServerConfigKey, ServerContent, ConfigKey, Shell, Command, UnityVersion, UnityModule, ContentMode, UnityStream, PackageList }

internal sealed record CliOption(string Flag, string Help, string? Value = null, string? Short = null, ArgKind ValueKind = ArgKind.None)
{
    public bool TakesValue => Value is not null;
    public string Display => (Short is null ? "" : Short + ", ") + Flag + (Value is null ? "" : " " + Value);
}

internal sealed record CliExample(string Command, string Text);

internal sealed class UsageException(string message) : Exception(message);

internal sealed class CliCommand
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string Summary { get; init; }
    public required Func<List<string>, Task> Run { get; init; }
    public string[] Aliases { get; init; } = Array.Empty<string>();
    public string[] Usage { get; init; } = Array.Empty<string>();
    public string? Details { get; init; }
    public CliOption[] Options { get; init; } = Array.Empty<CliOption>();
    public CliExample[] Examples { get; init; } = Array.Empty<CliExample>();
    public string[] SeeAlso { get; init; } = Array.Empty<string>();
    public ArgKind[] Args { get; init; } = Array.Empty<ArgKind>();
    public Dictionary<string, ArgKind[]>? Verbs { get; init; }
    public bool Json { get; init; }
    public bool Interactive { get; init; }
    public bool Hidden { get; init; }
    public bool RepeatLastArg { get; init; }

    public IEnumerable<string> Names => Aliases.Prepend(Name);

    public CliOption? FindOption(string flag) => Options.FirstOrDefault(o => o.Flag.Equals(flag, StringComparison.OrdinalIgnoreCase) || (o.Short?.Equals(flag, StringComparison.Ordinal) ?? false));

    public ArgKind ArgAt(IReadOnlyList<string> positionals)
    {
        var index = positionals.Count;
        if (Verbs is not null)
        {
            if (index == 0) return ArgKind.Verb;
            if (!Verbs.TryGetValue(positionals[0].ToLowerInvariant(), out var verbArgs)) return ArgKind.None;
            return index - 1 < verbArgs.Length ? verbArgs[index - 1] : RepeatLastArg && verbArgs.Length > 0 ? verbArgs[^1] : ArgKind.None;
        }
        return index < Args.Length ? Args[index] : RepeatLastArg && Args.Length > 0 ? Args[^1] : ArgKind.None;
    }
}
