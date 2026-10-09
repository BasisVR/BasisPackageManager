using System.Diagnostics;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const ushort DefaultServerPort = 4296;
    private const string DotNetDownload = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private bool _foregroundInterrupted;

    private async Task ListServerPackagesAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadServerInstallAsync();
        var packages = await _serverPackages.ListAsync(install.RepoRoot);
        if (Out.JsonMode)
        {
            Out.Json(packages.Select(p => new { id = p.Id, name = p.DisplayName, version = p.Version, status = p.Status, source = p.Source, commit = p.Commit, linkedFolder = p.LinkedFolder, folder = p.Folder, assemblies = p.Modules.Select(m => m.Assembly).Distinct(), detail = p.Detail }));
            return;
        }
        if (packages.Count == 0)
        {
            Out.Say("No server packages are installed.");
            Out.Hint($"Add one with '{Prefix}server-install <package-id|git-url|file:path>'.");
            return;
        }
        foreach (var package in packages)
        {
            Out.Say(new Span(package.Id, Tone.Bold), new Span("  " + package.Version, Tone.Dim), new Span("  " + DescribeServerStatus(package.Status), package.Status == ServerPackageStatus.Ready ? Tone.Good : Tone.Warn),
                new Span(package.ShortCommit.Length > 0 ? "  @ " + package.ShortCommit : "", Tone.Dim));
            Out.Note($"    {(package.IsLinked ? "built from " + package.LinkedFolder : package.Source)}");
            if (package.Assemblies.Length > 0) Out.Note($"    compiled into {package.Assemblies}");
            if (!string.IsNullOrWhiteSpace(package.Detail)) Out.Say(new Span("    ! ", Tone.Warn), new Span(package.Detail));
        }
    }

    private async Task InstallServerPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var link = TakeValue(options, "--link");
        Expect(options, 1, 1, "a package id, git URL or file: path");
        var install = await LoadServerInstallAsync();
        var (source, expectedId) = await ResolveServerSourceAsync(install, options[0]);
        ServerPackageResult result = null!;
        if (link is not null) result = await _serverPackages.InstallLinkedAsync(install.RepoRoot, source, FullPath(link), expectedId);
        else await RunOperationAsync(async ct => result = await _serverPackages.InstallAsync(install.RepoRoot, source, expectedId, Out.Progress, ct), "Adding the server package" + Out.Ellipsis);
        Report(result);
        Out.Hint($"Run '{Prefix}server-build' (or build the server) to compile it in.");
    }

    private async Task<(string Source, string? ExpectedId)> ResolveServerSourceAsync(BasisInstall install, string value)
    {
        var looksLikeId = ServerPackageService.IsValidId(value.Trim()) && !value.Contains('/') && !value.Contains(':');
        var resolved = ServerPackageService.ResolveSource(install.RepoRoot, value, looksLikeId ? await LoadCatalogAsync() : null);
        if (resolved.Error is not null) throw new InvalidOperationException(resolved.Error);
        return resolved.Entry is null ? (resolved.Source, resolved.ExpectedId) : (await LatestReleaseUrlAsync(resolved.Source) ?? resolved.Source, resolved.ExpectedId);
    }

    private async Task UpdateServerPackagesAsync(List<string> values)
    {
        var options = values.ToList();
        var force = TakeFlag(options, "--force");
        var gitRef = TakeValue(options, "--ref");
        Expect(options, gitRef is null ? 0 : 1, 1, "the package to move to that ref");
        var install = await LoadServerInstallAsync();
        var ids = options.Count == 1 ? options.ToArray() : _serverPackages.LoadManifest(install.RepoRoot).Dependencies.Keys.ToArray();
        if (ids.Length == 0)
        {
            Out.Success("No server packages are installed.");
            return;
        }
        var failures = 0;
        foreach (var id in ids)
        {
            ServerPackageResult result = null!;
            await RunOperationAsync(async ct => result = await _serverPackages.UpdateAsync(install.RepoRoot, id, gitRef, force, Out.Progress, ct), $"Updating {id}" + Out.Ellipsis);
            if (result.Ok) Out.Success(result.Message);
            else { Out.Error(result.Message); failures++; }
        }
        if (failures > 0) throw new InvalidOperationException($"{Plural(failures, "server package")} could not be updated.");
    }

    private async Task RemoveServerPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var force = TakeFlag(options, "--force");
        Expect(options, 1, 1, "a package id");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.RemoveAsync(install.RepoRoot, options[0], force));
    }

    private async Task RestoreServerPackagesAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadServerInstallAsync();
        ServerPackageResult result = null!;
        await RunOperationAsync(async ct => result = await _serverPackages.RestoreAsync(install.RepoRoot, Out.Progress, ct), "Restoring server packages" + Out.Ellipsis);
        Report(result);
    }

    private async Task LinkServerPackageAsync(List<string> values)
    {
        Expect(values, 2, 2, "a package id and a folder");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.LinkAsync(install.RepoRoot, values[0], FullPath(values[1])));
    }

    private async Task UnlinkServerPackageAsync(List<string> values)
    {
        Expect(values, 1, 1, "a package id");
        var install = await LoadServerInstallAsync();
        Report(await _serverPackages.UnlinkAsync(install.RepoRoot, values[0]));
    }

    private async Task BuildServerAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        await BuildServerCoreAsync(install);
    }

    private async Task BuildServerCoreAsync(BasisInstall install)
    {
        if (!_server.HasServerProject(install.RepoRoot)) throw new InvalidOperationException(BasisServerService.MissingProjectMessage);
        if (!await _server.HasRequiredDotNetSdkAsync()) throw new InvalidOperationException($"The .NET 10 SDK is required to build the Basis server. Get it from {DotNetDownload}");
        var result = (Success: false, Output: "");
        await RunOperationAsync(async ct => result = await _server.BuildAsync(install.RepoRoot, ct), "Building the Basis server" + Out.Ellipsis);
        if (!result.Success)
            throw new InvalidOperationException("The server build failed:\n" + string.Join('\n', result.Output.Split('\n').TakeLast(25)));
        Out.Success($"Built the Basis server into {_server.GetPaths(install.RepoRoot).RuntimeDirectory}.");
    }

    private async Task RunServerAsync(List<string> values)
    {
        var options = values.ToList();
        var build = TakeFlag(options, "--build");
        var newWindow = TakeFlag(options, "--new-window");
        Expect(options, 0, 0, "");
        var install = await LoadInstallAsync();
        if (!_server.HasServerProject(install.RepoRoot)) throw new InvalidOperationException(BasisServerService.MissingProjectMessage);
        var paths = _server.GetPaths(install.RepoRoot);
        if (!File.Exists(paths.ExecutablePath))
        {
            if (!build) throw new InvalidOperationException($"The Basis server hasn't been built yet. Run '{Prefix}server-build' first, or add --build.");
            await BuildServerCoreAsync(install);
        }
        if (await _server.IsReadyAsync(install.RepoRoot, TimeSpan.FromMilliseconds(500)))
            Out.Warning("A Basis server already answers on this project's health endpoint, so a second one may not get its port.");
        if (newWindow)
        {
            using var started = _server.Start(install.RepoRoot);
            Out.Success($"Started the Basis server in its own window (process {started.Id}).");
            if (!File.Exists(paths.ConfigFile)) Out.Hint("Its setup wizard asks for the server's settings in that window on this first run.");
            return;
        }
        Directory.CreateDirectory(paths.RuntimeDirectory);
        Out.Hint($"Starting the Basis server from {paths.RuntimeDirectory}. Press Ctrl+C to stop it.");
        using var process = Process.Start(new ProcessStartInfo(paths.ExecutablePath) { WorkingDirectory = paths.RuntimeDirectory, UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not start the Basis server.");
        _foregroundInterrupted = false;
        _foreground = process;
        try { await process.WaitForExitAsync(); }
        finally { _foreground = null; }
        if (process.ExitCode == 0 || _foregroundInterrupted) Out.Success("The Basis server stopped.");
        else
        {
            Out.Warning($"The Basis server exited with code {process.ExitCode}.");
            _exitCode = 1;
        }
    }

    private async Task ServerConfigAsync(List<string> values)
    {
        var options = values.ToList();
        var showSecrets = TakeFlag(options, "--show-secrets");
        Expect(options, 0, 2, "a setting name");
        var install = await LoadInstallAsync();
        if (!_server.HasServerProject(install.RepoRoot)) throw new InvalidOperationException(BasisServerService.MissingProjectMessage);
        var fields = _server.LoadConfig(install.RepoRoot);
        if (fields.Count == 0) throw new InvalidOperationException($"There's no config.xml yet. Run the server once with '{Prefix}server-run' and its setup wizard creates it.");
        if (options.Count == 0)
        {
            if (Out.JsonMode)
            {
                Out.Json(fields.Select(f => new { name = f.Name, value = IsSecret(f.Name) && !showSecrets ? null : f.Value, secret = IsSecret(f.Name), description = f.Description }));
                return;
            }
            Out.Table(fields.Select(f => new[]
            {
                new Span(f.Name, Tone.Accent), new Span(IsSecret(f.Name) && !showSecrets ? (f.Value.Length == 0 ? "(empty)" : "********") : f.Value),
                new Span(Out.Piped ? f.Description : Out.Truncate(f.Description, Math.Max(20, Out.Width - 60)), Tone.Dim),
            }).ToList(), indent: 0);
            if (!Out.Piped) Out.Hint($"Change one with '{Prefix}server-config <name> <value>'; passwords and keys are hidden unless you add --show-secrets.");
            return;
        }
        var field = fields.FirstOrDefault(f => f.Name.Equals(options[0], StringComparison.OrdinalIgnoreCase));
        if (field is null)
        {
            var suggestions = Suggest(options[0], fields.Select(f => f.Name));
            throw new InvalidOperationException($"config.xml has no setting '{options[0]}'." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" Run '{Prefix}server-config' to list them."));
        }
        if (options.Count == 1)
        {
            if (Out.JsonMode) Out.Json(new { name = field.Name, value = field.Value, description = field.Description });
            else
            {
                Out.Line(field.Value);
                if (field.Description.Length > 0) Out.Hint(field.Description);
            }
            return;
        }
        _server.SaveConfig(install.RepoRoot, fields.Select(f => f.Name == field.Name ? f with { Value = options[1] } : f));
        Out.Success($"Set {field.Name}. Restart the server to apply it.");
    }

    private static bool IsSecret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Key", StringComparison.OrdinalIgnoreCase);

    private async Task ServerContentAsync(List<string> values)
    {
        var options = values.ToList();
        var mode = TakeValue(options, "--mode");
        var password = TakeValue(options, "--password");
        var startup = TakeFlag(options, "--startup");
        var install = await LoadInstallAsync();
        if (!_server.HasServerProject(install.RepoRoot)) throw new InvalidOperationException(BasisServerService.MissingProjectMessage);
        var items = _server.ListContent(install.RepoRoot);
        if (options.Count == 0)
        {
            if (mode is not null || password is not null || startup) throw new UsageException("--mode, --password and --startup go with 'server-content add <url>'.");
            if (Out.JsonMode)
            {
                Out.Json(items.Select(i => new { name = i.Name, path = i.FullPath, defaultLibrary = i.IsDefaultLibrary }));
                return;
            }
            if (items.Count == 0)
            {
                Out.Say("The server has no default library items or startup content.");
                Out.Hint($"Add some with '{Prefix}server-content add <url>'.");
                return;
            }
            Out.Table(items.Select(i => new[] { new Span(i.Name), new Span(i.IsDefaultLibrary ? "default library" : "loaded at startup", Tone.Dim) }).ToList(), indent: 0);
            return;
        }
        switch (options[0].ToLowerInvariant())
        {
            case "add":
                Expect(options, 2, 2, "the content's URL");
                var kind = (mode ?? "avatar").ToLowerInvariant() switch
                {
                    "avatar" => 0,
                    "world" or "scene" => 1,
                    "prop" => 2,
                    var other => throw new UsageException($"'{other}' isn't a content mode. Use avatar, world or prop."),
                };
                var file = startup
                    ? _server.AddInitialResource(install.RepoRoot, kind switch { 0 => 2, 2 => 0, _ => 1 }, options[1], password ?? "")
                    : _server.AddDefaultLibraryItem(install.RepoRoot, kind, options[1], password ?? "");
                Out.Success($"Added {Path.GetFileName(file)} to the {(startup ? "startup content" : "default library")}. Restart the server to load it.");
                break;
            case "remove":
                Expect(options, 2, 2, "the content file to remove");
                var item = items.FirstOrDefault(i => i.Name.Equals(options[1], StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"There's no server content called {options[1]}. Run '{Prefix}server-content' to list it.");
                _server.RemoveContent(install.RepoRoot, item);
                Out.Success($"Removed {item.Name}. Restart the server to drop it.");
                break;
            default:
                throw new UsageException($"'server-content {options[0]}' isn't something server-content can do. Use add or remove.");
        }
    }

    private async Task ConnectAsync(List<string> values)
    {
        var options = values.ToList();
        var password = TakeValue(options, "--password");
        var wait = TakeFlag(options, "--wait");
        Expect(options, 0, 1, "a server address");
        var local =options.Count == 0 || BasisServerService.IsLocalHost(SplitAddress(options[0], DefaultServerPort).Host);
        var install = local ? await LoadInstallAsync() : await TryLoadInstallAsync();
        var defaultPort = local && install is not null && _server.LoadConfig(install.RepoRoot).FirstOrDefault(f => f.Name == "SetPort") is { } setPort && ushort.TryParse(setPort.Value, out var configured) && configured > 0 ? configured : DefaultServerPort;
        var (host, port) = SplitAddress(options.Count == 1 ? options[0] : null, defaultPort);
        if (local && install is not null)
        {
            var ready = await _server.IsReadyAsync(install.RepoRoot, TimeSpan.FromMilliseconds(750));
            if (!ready && wait)
            {
                await RunOperationAsync(async ct =>
                {
                    var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
                    while (!ready && DateTime.UtcNow < until)
                    {
                        await Task.Delay(500, ct);
                        ready = await _server.IsReadyAsync(install.RepoRoot, TimeSpan.FromSeconds(1), ct);
                    }
                }, "Waiting for the local Basis server" + Out.Ellipsis);
                if (!ready) throw new InvalidOperationException("The local Basis server didn't become ready within 10 minutes. Check its console.");
            }
            else if (!ready) Out.Warning($"The local Basis server doesn't answer yet. Start it with '{Prefix}server-run' in another terminal, or add --wait.");
        }
        var connection = local && install is not null ? _server.BuildClientConnection(install.RepoRoot, host, port, password) : BasisServerService.BuildConnection(host, port, password);
        if (!_server.LaunchSteamClient(connection)) throw new InvalidOperationException("Steam could not be found. Install Steam, or add it to PATH.");
        Out.Success($"Launching Basis Labs through Steam and joining {host}:{port}.");
    }

    internal static (string Host, ushort Port) SplitAddress(string? value, ushort defaultPort)
    {
        if (string.IsNullOrWhiteSpace(value)) return ("127.0.0.1", defaultPort);
        var text = value.Trim();
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0) throw new UsageException($"'{text}' has an unclosed '['.");
            var rest = text[(end + 1)..];
            return (text[1..end], rest.StartsWith(':') ? ParsePort(rest[1..]) : defaultPort);
        }
        var colon = text.IndexOf(':');
        if (colon >= 0 && colon == text.LastIndexOf(':')) return (colon == 0 ? "127.0.0.1" : text[..colon], ParsePort(text[(colon + 1)..]));
        return (text, defaultPort);
    }

    private static ushort ParsePort(string text) => ushort.TryParse(text, out var port) && port > 0 ? port : throw new UsageException($"'{text}' isn't a port number.");

    private async Task<BasisInstall> LoadServerInstallAsync()
    {
        var install = await LoadInstallAsync();
        if (!ServerPackageService.HasServer(install.RepoRoot))
            throw new InvalidOperationException("This project has no Basis Server source (Basis Server/BasisNetworkCore).");
        return install;
    }

    private static void Report(ServerPackageResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(result.Message);
        Out.Success(result.Message);
    }

    private static string DescribeServerStatus(ServerPackageStatus status) => status switch
    {
        ServerPackageStatus.Ready => "ready",
        ServerPackageStatus.NotRestored => "not restored",
        ServerPackageStatus.Changed => "changed",
        ServerPackageStatus.Missing => "missing",
        _ => "invalid",
    };
}
