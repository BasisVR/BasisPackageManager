using System.Text;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private static readonly string[] GlobalFlags = { "--project", "--json", "--no-color", "--help", "--version" };
    private static readonly string[] Shells = { "powershell", "bash", "zsh", "fish" };
    internal static readonly string[] UnityModules =
    {
        "windows-il2cpp", "windows-mono", "android", "android-sdk-ndk-tools", "android-open-jdk", "linux-il2cpp", "linux-mono", "linux-server",
        "mac-il2cpp", "mac-mono", "mac-server", "windows-server", "ios", "webgl", "universal-windows-platform", "documentation",
    };

    internal static (List<string> Words, string Current, int Start) SplitForCompletion(string line)
    {
        var words = new List<string>();
        var token = new StringBuilder();
        char? quote = null;
        var inToken = false;
        var start = line.Length;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else token.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { words.Add(token.ToString()); token.Clear(); inToken = false; }
                continue;
            }
            var first = !inToken;
            if (!inToken) { inToken = true; start = i; }
            if (c == '"' || c == '\'' && first) quote = c;
            else token.Append(c);
        }
        return inToken ? (words, token.ToString(), start) : (words, "", line.Length);
    }

    private CompletionResult CompleteForEditor(string line)
    {
        try
        {
            var (words, current, start) = SplitForCompletion(line);
            if (words.Count > 0 && words[0].Equals("basispm", StringComparison.OrdinalIgnoreCase)) words.RemoveAt(0);
            return new CompletionResult(start, Candidates(words, current, shell: false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write("Completing a console command", ex);
            return CompletionResult.None;
        }
    }

    internal IReadOnlyList<string> Candidates(List<string> words, string current, bool shell)
    {
        var project = TakeCompletionProject(words);
        var all = CandidatesFor(words, current, shell, project);
        return all.Where(c => c.StartsWith(current, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c.StartsWith('-') ? 1 : 0).ThenBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? TakeCompletionProject(List<string> words)
    {
        string? project = null;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].StartsWith("--project=", StringComparison.OrdinalIgnoreCase)) { project = words[i]["--project=".Length..]; words.RemoveAt(i--); }
            else if (words[i].Equals("--project", StringComparison.OrdinalIgnoreCase) && i + 1 < words.Count) { project = words[i + 1]; words.RemoveRange(i--, 2); }
            else if (words[i] is "--json" or "--no-color") words.RemoveAt(i--);
        }
        return project;
    }

    private IEnumerable<string> CandidatesFor(List<string> words, string current, bool shell, string? project)
    {
        if (words.Count > 0 && words[^1].Equals("--project", StringComparison.OrdinalIgnoreCase)) return ArgCandidates(ArgKind.Project, null, current, shell, project);
        if (words.Count == 0)
        {
            if (current.StartsWith('-')) return GlobalFlags;
            var visible = VisibleCommands.ToList();
            return current.Length == 0 ? visible.Select(c => c.Name) : visible.SelectMany(c => c.Names);
        }
        var command = FindCommand(words[0]);
        if (command is null || command.Hidden) return Array.Empty<string>();
        var rest = words.Skip(1).ToList();
        if (rest.Count > 0 && command.FindOption(rest[^1]) is { TakesValue: true } valueOption) return ArgCandidates(valueOption.ValueKind, command, current, shell, project);
        if (current.StartsWith('-'))
        {
            var flags = command.Options.Select(o => o.Flag).Append("--help");
            return command.Json ? flags.Append("--json") : flags;
        }
        var positionals = new List<string>();
        for (var i = 0; i < rest.Count; i++)
        {
            if (rest[i].StartsWith('-') && rest[i].Length > 1)
            {
                if (command.FindOption(rest[i]) is { TakesValue: true }) i++;
                continue;
            }
            positionals.Add(rest[i]);
        }
        return ArgCandidates(command.ArgAt(positionals), command, current, shell, project);
    }

    private IEnumerable<string> ArgCandidates(ArgKind kind, CliCommand? command, string current, bool shell, string? project)
    {
        switch (kind)
        {
            case ArgKind.Verb: return command?.Verbs?.Keys ?? Enumerable.Empty<string>();
            case ArgKind.Project: return SavedProjects(_settings.Load()).Select(p => p.Name);
            case ArgKind.Command: return VisibleCommands.Select(c => c.Name);
            case ArgKind.Shell: return Shells;
            case ArgKind.ConflictChoice: return new[] { "mine", "basis", "theirs", "done" };
            case ArgKind.ConfigKey: return SettingKeys.Select(k => k.Key);
            case ArgKind.ContentMode: return new[] { "avatar", "world", "prop" };
            case ArgKind.UnityStream: return new[] { "lts", "supported", "tech", "beta", "alpha" };
            case ArgKind.UnityModule: return UnityModules;
            case ArgKind.Path: return shell ? Enumerable.Empty<string>() : PathCandidates(current);
        }
        var root = CompletionRoot(project);
        if (root is null) return kind == ArgKind.CatalogPackage && _catalog is not null ? _catalog.Packages.Keys : Enumerable.Empty<string>();
        try
        {
            return kind switch
            {
                ArgKind.InstalledPackage => InstalledIds(root),
                ArgKind.Package => _catalog is null ? InstalledIds(root) : InstalledIds(root).Concat(_catalog.Packages.Keys),
                ArgKind.CatalogPackage => _catalog?.Packages.Keys ?? Enumerable.Empty<string>(),
                ArgKind.ProjectBranch => Wait(_updates.ListProjectBranchesAsync(root)).Where(b => !b.IsCurrent).Select(b => b.Display),
                ArgKind.BasisBranch => BasisBranchCandidates(root, shell),
                ArgKind.ConflictPath => Wait(_updates.GetConflictsAsync(root)).Select(c => c.Path),
                ArgKind.ServerPackage => ServerPackageService.HasServer(root) ? _serverPackages.LoadManifest(root).Dependencies.Keys : Enumerable.Empty<string>(),
                ArgKind.ServerConfigKey => _server.LoadConfig(root).Select(f => f.Name),
                ArgKind.ServerContent => _server.ListContent(root).Select(f => f.Name),
                ArgKind.UnityVersion => UnityVersionCandidates(root),
                _ => Enumerable.Empty<string>(),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Completing {kind} values", ex);
            return Enumerable.Empty<string>();
        }
    }

    private string? CompletionRoot(string? project)
    {
        try
        {
            var settings = _settings.Load();
            if (project is not null) return PickProject(settings, project).Root;
            if (_sessionProject is not null) return _sessionProject;
            if (Environment.GetEnvironmentVariable("BASISPM_PROJECT") is { Length: > 0 } fromEnvironment) return PickProject(settings, fromEnvironment).Root;
            return FindProjectAt(settings, Environment.CurrentDirectory) ?? DefaultProject(settings) ?? OnlyProject(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DiagnosticLog.Write("Finding the project to complete names from", ex);
            return null;
        }
    }

    private IEnumerable<string> InstalledIds(string root)
    {
        var detection = _projects.Detect(root);
        if (!detection.IsValid || detection.ResolvedPath is null) return Enumerable.Empty<string>();
        var unity = detection.ResolvedPath;
        var manifest = Wait(_projects.LoadAsync(unity)).Manifest;
        return manifest.Dependencies.Keys.Concat(_projects.ListEmbeddedPackages(unity).Select(p => p.Id)).Concat(_mounts.ListMounts(unity).Select(m => m.PackageId));
    }

    private IEnumerable<string> BasisBranchCandidates(string root, bool shell)
    {
        if (!shell)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { return BasisUpdateService.OrderBranches(Wait(_updates.GetBasisHeadsAsync(false, timeout.Token)).Keys); }
            catch (OperationCanceledException) { }
        }
        var refs = Wait(_git.ListRefsAsync(root, new[] { BasisUpdateService.UpstreamRefPrefix.TrimEnd('/') }, 64));
        return refs.Select(r => r[BasisUpdateService.UpstreamRefPrefix.Length..]).Append(BasisInstallService.DefaultBranch);
    }

    private IEnumerable<string> UnityVersionCandidates(string root)
    {
        var settings = _settings.Load();
        var detection = _projects.Detect(root);
        var required = detection.IsValid && detection.ResolvedPath is not null ? Wait(_projects.LoadAsync(detection.ResolvedPath)).UnityVersion : null;
        return settings.ManualEditors.Select(m => m.Version).Prepend(required ?? "").Where(v => v.Length > 0 && v != "unknown");
    }

    private static IEnumerable<string> PathCandidates(string current)
    {
        try
        {
            var expanded = current.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + current[1..] : current;
            var folder = expanded.EndsWith(Path.DirectorySeparatorChar) || expanded.EndsWith('/') ? expanded : Path.GetDirectoryName(expanded);
            var search = string.IsNullOrEmpty(folder) ? "." : folder;
            if (!Directory.Exists(search)) return Enumerable.Empty<string>();
            var name = expanded.EndsWith(Path.DirectorySeparatorChar) || expanded.EndsWith('/') ? "" : Path.GetFileName(expanded);
            var head = current[..(current.Length - name.Length)];
            return new DirectoryInfo(search).EnumerateFileSystemInfos()
                .Where(e => e.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase) && (name.StartsWith('.') || !e.Name.StartsWith('.')))
                .Take(500).Select(e => head + e.Name + (e is DirectoryInfo ? Path.DirectorySeparatorChar.ToString() : "")).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiagnosticLog.Write($"Completing the path {current}", ex);
            return Enumerable.Empty<string>();
        }
    }

    private static T Wait<T>(Task<T> task) => task.GetAwaiter().GetResult();

    private Task CompleteAsync(List<string> values)
    {
        var options = values.ToList();
        var shell = TakeValue(options, "--shell")?.ToLowerInvariant() ?? "plain";
        var line = Environment.GetEnvironmentVariable("COMP_LINE");
        if (line is null) line = string.Join(" ", options);
        else if (int.TryParse(Environment.GetEnvironmentVariable("COMP_POINT"), out var point) && point >= 0)
            line = point <= line.Length ? line[..point] : line + new string(' ', point - line.Length);
        var (words, current, _) = SplitForCompletion(line);
        if (words.Count > 0) words.RemoveAt(0);
        var context = words.ToList();
        var output = new System.Text.StringBuilder();
        foreach (var candidate in Candidates(words, current, shell: true))
        {
            var text = shell == "bash" ? candidate.Replace(" ", "\\ ") : candidate;
            var help = shell is "powershell" or "fish" ? CandidateHelp(context, current, candidate) : null;
            output.Append(help is null ? text : text + "\t" + help).Append('\n');
        }
        Console.Out.Write(output.ToString());
        return Task.CompletedTask;
    }

    private string? CandidateHelp(List<string> words, string current, string candidate)
    {
        TakeCompletionProject(words);
        if (words.Count == 0) return current.StartsWith('-') ? null : FindCommand(candidate)?.Summary;
        var command = FindCommand(words[0]);
        if (command is null) return null;
        if (!candidate.StartsWith('-')) return null;
        return candidate == "--help" ? "Show this command's options and examples" : candidate == "--json" ? "Print the result as JSON" : command.FindOption(candidate)?.Help;
    }

    private Task CompletionAsync(List<string> values)
    {
        Expect(values, 0, 1, "a shell: powershell, bash, zsh or fish");
        var exe = Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path).Equals("basispm", StringComparison.OrdinalIgnoreCase) ? path : "basispm";
        var shell = values.Count == 1 ? values[0].ToLowerInvariant()
            : OperatingSystem.IsWindows() ? "powershell" : Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "bash");
        var script = shell switch
        {
            "powershell" or "pwsh" => PowerShellScript(exe),
            "bash" => BashScript(exe),
            "zsh" => ZshScript(exe),
            "fish" => FishScript(exe),
            var other => throw new UsageException($"'{other}' isn't a shell this knows. Pick powershell, bash, zsh or fish."),
        };
        Console.Out.Write(script);
        return Task.CompletedTask;
    }

    private static string Single(string text) => "'" + text.Replace("'", "''") + "'";

    private static string Posix(string text) => "'" + text.Replace("'", "'\\''") + "'";

    internal static string PowerShellScript(string exe) => $$"""
        Register-ArgumentCompleter -Native -CommandName 'basispm', 'basispm.exe' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $env:COMP_LINE = $commandAst.Extent.Text
            $env:COMP_POINT = $cursorPosition - $commandAst.Extent.StartOffset
            try { $lines = @(& {{Single(exe)}} __complete --shell powershell 2>$null) }
            finally { Remove-Item Env:COMP_LINE, Env:COMP_POINT -ErrorAction SilentlyContinue }
            foreach ($line in $lines) {
                $text, $help = $line -split "`t", 2
                if (-not $text) { continue }
                $completion = if ($text -match '[\s''"]') { "'" + ($text -replace "'", "''") + "'" } else { $text }
                [System.Management.Automation.CompletionResult]::new($completion, $text, 'ParameterValue', $(if ($help) { $help } else { $text }))
            }
        }

        """;

    internal static string BashScript(string exe) => $$"""
        _basispm_complete() {
            local IFS=$'\n'
            read -d '' -ra COMPREPLY < <(COMP_LINE="$COMP_LINE" COMP_POINT="$COMP_POINT" {{Posix(exe)}} __complete --shell bash 2>/dev/null | tr -d '\r')
            return 0
        }
        complete -o default -F _basispm_complete basispm basispm.exe

        """;

    internal static string ZshScript(string exe) => $$"""
        _basispm() {
            local -a candidates
            local line="${(j: :)words[1,CURRENT]}"
            candidates=("${(@f)$(COMP_LINE="$line" COMP_POINT=${#line} {{Posix(exe)}} __complete --shell zsh 2>/dev/null)}")
            if [[ -n "${candidates[1]}" ]]; then
                compadd -- "${candidates[@]}"
            else
                _files
            fi
        }
        compdef _basispm basispm basispm.exe

        """;

    internal static string FishScript(string exe) => $$"""
        function __basispm_complete
            set -lx COMP_LINE (commandline -cp)
            set -lx COMP_POINT (string length -- "$COMP_LINE")
            set -l candidates ({{Posix(exe)}} __complete --shell fish 2>/dev/null)
            if test (count $candidates) -gt 0
                printf '%s\n' $candidates
            else
                __fish_complete_path (commandline -ct)
            end
        end
        complete -c basispm -f -a '(__basispm_complete)'

        """;
}
