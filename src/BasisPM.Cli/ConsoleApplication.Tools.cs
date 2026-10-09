using System.Runtime.InteropServices;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    internal sealed record SettingKey(string Key, string Help, Func<UserSettings, string?> Get, Action<UserSettings, string?> Set, Func<string, string?>? Check = null);

    internal static readonly SettingKey[] SettingKeys =
    {
        new("catalog-url", "Package registry to use; empty uses the Basis registry", s => s.CatalogUrl, (s, v) => s.CatalogUrl = v ?? "", v => GitUrlPolicy.IsWebUrl(v) ? null : "needs an http(s) URL"),
        new("extra-catalogs", "More registries, separated by commas", s => string.Join(",", s.ExtraCatalogUrls), (s, v) => s.ExtraCatalogUrls = SplitList(v),
            v => SplitList(v).FirstOrDefault(u => !GitUrlPolicy.IsWebUrl(u)) is { } bad ? $"'{bad}' isn't an http(s) URL" : null),
        new("unity-hub-path", "Unity Hub, when it isn't installed in the usual place", s => s.UnityHubPath, (s, v) => s.UnityHubPath = string.IsNullOrWhiteSpace(v) ? null : v.Trim(),
            v => File.Exists(v) || Directory.Exists(v) ? null : "points at nothing on this computer"),
        new("auto-check-basis-updates", "The app checks projects for Basis updates on start and every 2 hours", s => s.AutoCheckBasisUpdates ? "true" : "false",
            (s, v) => s.AutoCheckBasisUpdates = v is null || ParseBool(v) == true, v => ParseBool(v) is null ? "needs true or false" : null),
        new("prerelease-updates", "The app updates itself to prereleases too", s => s.PrereleaseUpdates ? "true" : "false",
            (s, v) => s.PrereleaseUpdates = v is not null && ParseBool(v) == true, v => ParseBool(v) is null ? "needs true or false" : null),
        new("language", "The app's language code, such as en or de; empty follows the system", s => s.Language, (s, v) => s.Language = string.IsNullOrWhiteSpace(v) ? null : v.Trim()),
    };

    private static List<string> SplitList(string? value) => (value ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null,
    };

    private async Task ConfigAsync(List<string> values)
    {
        if (values.Count == 0)
        {
            var settings = await _settings.LoadAsync();
            if (Out.JsonMode)
            {
                Out.Json(SettingKeys.ToDictionary(k => k.Key, k => k.Get(settings)));
                return;
            }
            Out.Table(SettingKeys.Select(k => new[]
            {
                new Span(k.Key, Tone.Accent), string.IsNullOrEmpty(k.Get(settings)) ? new Span("(default)", Tone.Dim) : new Span(k.Get(settings)!), new Span(k.Help, Tone.Dim),
            }).ToList(), indent: 0);
            if (!Out.Piped) Out.Hint($"Settings live in {_settings.SettingsPath} and are shared with the desktop app.");
            return;
        }
        var verb = values[0].ToLowerInvariant();
        if (verb == "path")
        {
            Expect(values, 1, 1, "");
            if (Out.JsonMode) Out.Json(new { path = _settings.SettingsPath });
            else Out.Line(_settings.SettingsPath);
            return;
        }
        if (verb is not ("get" or "set" or "unset")) throw new UsageException($"'config {values[0]}' isn't something config can do. Use get, set, unset or path.");
        Expect(values, 2, verb == "set" ? 3 : 2, verb == "set" ? "a key and a value" : "a key");
        var key = SettingKeys.FirstOrDefault(k => k.Key.Equals(values[1], StringComparison.OrdinalIgnoreCase))
            ?? throw new UsageException($"There's no setting '{values[1]}'." + (Suggest(values[1], SettingKeys.Select(k => k.Key)) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : $" Keys: {string.Join(", ", SettingKeys.Select(k => k.Key))}."));
        switch (verb)
        {
            case "get":
                var value = key.Get(await _settings.LoadAsync());
                if (Out.JsonMode) Out.Json(new { key = key.Key, value });
                else Out.Line(value ?? "");
                break;
            case "set":
                if (values.Count < 3) throw new UsageException($"Missing the value for {key.Key}.");
                if (key.Check?.Invoke(values[2]) is { } problem) throw new UsageException($"{key.Key} {problem}.");
                await _settings.UpdateAsync(s => key.Set(s, values[2]));
                if (key.Key is "catalog-url" or "extra-catalogs") _catalog = null;
                Out.Success($"Set {key.Key} to {key.Get(await _settings.LoadAsync())}.");
                Out.Hint("If the desktop app is open, restart it so it picks this up.");
                break;
            default:
                await _settings.UpdateAsync(s => key.Set(s, null));
                if (key.Key is "catalog-url" or "extra-catalogs") _catalog = null;
                Out.Success($"{key.Key} is back to its default.");
                break;
        }
    }

    private sealed record DoctorCheck(string Area, DoctorLevel Level, string Title, string? Detail = null, string? Fix = null);

    private enum DoctorLevel { Ok, Info, Warn, Fail }

    private async Task DoctorAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var settings = await _settings.LoadAsync();
        BasisInstall? install = null;
        string? projectProblem = null;
        try { install = await LoadInstallAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { projectProblem = ex.Message; }
        var checks = new List<DoctorCheck>();
        await RunOperationAsync(async ct =>
        {
            var gitVersion = _git.IsAvailable ? _git.GetVersionAsync(ct) : Task.FromResult<Version?>(null);
            var hub = _unityHub.FindHubPath(settings.UnityHubPath);
            using var hubTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hubTimeout.CancelAfter(TimeSpan.FromSeconds(40));
            var editors = hub is null ? Task.FromResult<IReadOnlyList<InstalledEditor>>(Array.Empty<InstalledEditor>()) : _unityHub.ListInstalledAsync(settings.UnityHubPath, hubTimeout.Token);
            var sdk = install is not null && _server.HasServerProject(install.RepoRoot) ? _server.HasRequiredDotNetSdkAsync(ct) : Task.FromResult(true);
            var catalog = _catalogs.TryLoadAsync(string.IsNullOrWhiteSpace(settings.CatalogUrl) ? CatalogService.DefaultCatalogUrl : settings.CatalogUrl, ct);
            var token = TryGitHubTokenAsync();
            await Task.WhenAll(Quiet(gitVersion), Quiet(editors), Quiet(sdk), Quiet(catalog), Quiet(token));

            var git = _git.FindGit();
            var version = gitVersion.IsCompletedSuccessfully ? gitVersion.Result : null;
            checks.Add(git is null ? new("Tools", DoctorLevel.Fail, "Git not found", "Basis Package Manager uses git for clones, updates and packages.", "Install it from https://git-scm.com/ and make sure it's on your PATH")
                : version is not null && version < BasisUpdateService.MinimumGitVersion ? new("Tools", DoctorLevel.Fail, $"Git {version.ToString(3)} is too old", $"Basis updates need {BasisUpdateService.MinimumGitVersion.ToString(2)} or newer.", "Update Git from https://git-scm.com/")
                : version is not null && version < BasisUpdateService.PreviewGitVersion ? new("Tools", DoctorLevel.Warn, $"Git {version.ToString(3)}", $"Git {BasisUpdateService.PreviewGitVersion.ToString(2)} or newer can predict update conflicts before they happen.", "Update Git from https://git-scm.com/")
                : new("Tools", DoctorLevel.Ok, $"Git {version?.ToString(3)}", git));
            var installed = (editors.IsCompletedSuccessfully ? editors.Result : Array.Empty<InstalledEditor>()).Select(e => e.Version).Concat(settings.ManualEditors.Select(m => m.Version)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            checks.Add(hub is not null ? new("Tools", DoctorLevel.Ok, "Unity Hub", hub)
                : settings.ManualEditors.Count > 0 ? new("Tools", DoctorLevel.Info, "Unity Hub not found", $"{Plural(settings.ManualEditors.Count, "editor")} added by folder can still open projects.")
                : new("Tools", DoctorLevel.Warn, "Unity Hub not found", "It installs Unity editors and modules.", $"Install it from {UnityHubDownload}, or run '{Prefix}config set unity-hub-path <path>'"));
            if (install is not null && _server.HasServerProject(install.RepoRoot))
                checks.Add(sdk.IsCompletedSuccessfully && sdk.Result ? new("Tools", DoctorLevel.Ok, ".NET 10 SDK", "can build the Basis server")
                    : new("Tools", DoctorLevel.Warn, ".NET 10 SDK not found", "It's needed to build the Basis server.", $"Get it from {DotNetDownload}"));
            checks.Add(token.IsCompletedSuccessfully && token.Result is not null ? new("Tools", DoctorLevel.Ok, "GitHub sign-in", "found a token for 'contribute'")
                : new("Tools", DoctorLevel.Info, "Not signed in to GitHub", "Only 'contribute' needs it.", "Run 'gh auth login' or set GH_TOKEN"));
            checks.Add(BasisServerService.FindSteamExecutable() is { } steam ? new("Tools", DoctorLevel.Ok, "Steam", steam) : new("Tools", DoctorLevel.Info, "Steam not found", "Only 'connect' needs it."));

            if (install is null) checks.Add(new("Project", DoctorLevel.Warn, "No project selected", projectProblem, $"Pass --project <name|path>, or add one with '{Prefix}projects add <path>'"));
            else
            {
                checks.Add(!install.HasUnityProject ? new("Project", DoctorLevel.Fail, "No Unity project found", install.RepoRoot)
                    : !install.IsBasisCheckout ? new("Project", DoctorLevel.Fail, "Not a complete Basis checkout", "Its Unity project has no Packages/com.basis.framework.")
                    : new("Project", DoctorLevel.Ok, "Basis checkout", install.UnityProjectPath));
                if (install.HasUnityProject && install.UnityVersion != "unknown")
                    checks.Add(installed.Contains(install.UnityVersion) ? new("Project", DoctorLevel.Ok, $"Unity {install.UnityVersion} installed")
                        : new("Project", DoctorLevel.Fail, $"Unity {install.UnityVersion} isn't installed", "The project needs that exact editor version.", $"{Prefix}unity install"));
                if (!install.IsGitRepo) checks.Add(new("Project", DoctorLevel.Warn, "Not tracked by git", "Basis updates need git.", $"{Prefix}update-basis --init-git"));
                else if (git is null) checks.Add(new("Project", DoctorLevel.Warn, "Can't read the project's git state", "Git isn't available."));
                else try
                {
                    var status = await _git.GetStatusAsync(install.RepoRoot, ct);
                    checks.Add(new("Project", DoctorLevel.Ok, $"On {status.Branch} @ {status.ShortCommit}", status.ChangeCount == 0 ? "no local changes" : Plural(status.ChangeCount, "changed file")));
                    if (await _updates.LoadStateAsync(install.RepoRoot, ct) is { } state)
                        checks.Add(new("Project", DoctorLevel.Warn, state.Operation == BasisOperation.BranchSwitch ? $"A switch to {state.TargetBranch} is waiting for decisions" : "A Basis update is waiting for decisions", null, $"{Prefix}conflicts"));
                    else if (await _git.GetOperationInProgressAsync(install.RepoRoot, ct) is { } busy)
                        checks.Add(new("Project", DoctorLevel.Warn, $"A git {busy} is in progress", "Updates and branch switches wait until it's finished or aborted."));
                    if (await _git.IsShallowAsync(install.RepoRoot, ct))
                        checks.Add(new("Project", DoctorLevel.Warn, "Shallow clone", "Its history can't be matched to Basis for updates.", "git fetch --unshallow"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException)
                {
                    DiagnosticLog.Write($"Reading the git state of {install.RepoRoot} for doctor", ex);
                    checks.Add(new("Project", DoctorLevel.Warn, "Can't read the project's git state", ex.Message));
                }
                var reconcile = _basisDev.Scan(install).WithIssues.Count;
                if (reconcile > 0) checks.Add(new("Project", DoctorLevel.Warn, $"{Plural(reconcile, "package")} to reconcile", "Development clones or mount records disagree with what Unity loads.", $"{Prefix}basisdev"));
                if (install.HasUnityProject && UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
                    checks.Add(new("Project", DoctorLevel.Info, "Open in Unity right now", "Close Unity before updating Basis or switching branches."));
            }
            var space = DiskSpace.ForPath(install?.RepoRoot ?? Environment.CurrentDirectory);
            if (space is not null)
                checks.Add(space.FreeBytes < 2L * 1024 * 1024 * 1024 ? new("Project", DoctorLevel.Fail, $"Only {DiskSpace.Human(space.FreeBytes)} free on {space.DriveName}", "Unity needs room for its Library cache.")
                    : space.FreeBytes < 15L * 1024 * 1024 * 1024 ? new("Project", DoctorLevel.Warn, $"{DiskSpace.Human(space.FreeBytes)} free on {space.DriveName}", "Unity's Library cache for Basis takes several GB.")
                    : new("Project", DoctorLevel.Ok, $"{DiskSpace.Human(space.FreeBytes)} free on {space.DriveName}"));
            var loaded = catalog.IsCompletedSuccessfully ? catalog.Result : null;
            var registry = string.IsNullOrWhiteSpace(settings.CatalogUrl) ? CatalogService.DefaultCatalogUrl : settings.CatalogUrl;
            checks.Add(loaded is not null ? new("Registry", DoctorLevel.Ok, $"Package registry: {Plural(loaded.Packages.Count, "package")}", registry)
                : new("Registry", DoctorLevel.Warn, "Package registry unreachable", $"{registry} didn't answer, so the built-in copy is used.", "Check your connection, or 'config get catalog-url'"));
        }, "Checking" + Out.Ellipsis);

        var fails = checks.Count(c => c.Level == DoctorLevel.Fail);
        var warns = checks.Count(c => c.Level == DoctorLevel.Warn);
        if (Out.JsonMode)
        {
            Out.Json(new { project = install?.DisplayName, checks = checks.Select(c => new { area = c.Area, level = c.Level, title = c.Title, detail = c.Detail, fix = c.Fix }), problems = fails, warnings = warns });
        }
        else
        {
            foreach (var area in checks.Select(c => c.Area).Distinct())
            {
                Out.Heading(area == "Project" && install is not null ? $"Project {install.DisplayName}" : area);
                foreach (var check in checks.Where(c => c.Area == area))
                {
                    var (mark, tone) = check.Level switch
                    {
                        DoctorLevel.Ok => (Out.Check, Tone.Good),
                        DoctorLevel.Info => (Out.Dot, Tone.Dim),
                        DoctorLevel.Warn => ("!", Tone.Warn),
                        _ => (Out.Cross, Tone.Bad),
                    };
                    Out.Say(new Span($"  {mark} ", tone), new Span(check.Title, check.Level == DoctorLevel.Ok || check.Level == DoctorLevel.Info ? Tone.Plain : tone), new Span(check.Detail is null ? "" : "  " + check.Detail, Tone.Dim));
                    if (check.Fix is not null && check.Level != DoctorLevel.Ok) Out.Say(new Span($"      {Out.Arrow} ", Tone.Dim), new Span(check.Fix, Tone.Accent));
                }
                Out.Say();
            }
            if (fails == 0 && warns == 0) Out.Success("No problems found.");
            else Out.Say(new Span(fails > 0 ? Out.Cross + " " : "! ", fails > 0 ? Tone.Bad : Tone.Warn), new Span(string.Join(" and ", new[] { fails > 0 ? Plural(fails, "problem") : null, warns > 0 ? Plural(warns, "warning") : null }.Where(s => s is not null)) + "."));
        }
        if (fails > 0) _exitCode = 1;
    }

    private static async Task Quiet(Task task)
    {
        try { await task; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { DiagnosticLog.Write("Running a doctor check", ex); }
    }

    private async Task LogsAsync(List<string> values)
    {
        var options = values.ToList();
        var pathOnly = TakeFlag(options, "--path");
        var count = TakeInt(options, "--lines", 1, 100000) ?? 40;
        Expect(options, 0, 0, "");
        var file = DiagnosticLog.FilePath;
        if (pathOnly)
        {
            Out.Line(file);
            return;
        }
        if (!File.Exists(file))
        {
            Out.Success("The diagnostic log is empty, so nothing has gone wrong yet.");
            Out.Hint(file);
            return;
        }
        var lines = new List<string>();
        await using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            while (await reader.ReadLineAsync() is { } line) lines.Add(line);
        foreach (var line in lines.Skip(Math.Max(0, lines.Count - count)))
            Out.Say(line.Length > 24 && char.IsDigit(line[0]) && line.Contains(" [handled] ", StringComparison.Ordinal) ? new Span(line, Tone.Accent) : new Span(line));
        Out.Hint($"{file} ({DiskSpace.Human(new FileInfo(file).Length)}). '{Prefix}logs --lines <count>' shows more.");
    }

    private async Task NewsAsync(List<string> values)
    {
        var options = values.ToList();
        var everything = TakeFlag(options, "--all");
        Expect(options, 0, 0, "");
        List<Announcement> items = null!;
        await RunOperationAsync(async ct => items = await new AnnouncementService().LoadAsync(null, ct), "Loading announcements" + Out.Ellipsis);
        var shown = everything ? items : items.Take(5).ToList();
        if (Out.JsonMode)
        {
            Out.Json(shown);
            return;
        }
        if (shown.Count == 0)
        {
            Out.Say("No announcements.");
            return;
        }
        foreach (var item in shown)
        {
            var tone = item.Level?.ToLowerInvariant() switch { "alert" => Tone.Bad, "update" => Tone.Good, _ => Tone.Accent };
            Out.Say(new Span(item.Date ?? "", Tone.Dim), new Span(item.Date is null ? "" : "  "), new Span(item.Title, tone), new Span(item.Pinned ? "  (pinned)" : "", Tone.Dim));
            foreach (var line in Out.Wrap(item.Body, Math.Max(40, Out.Width - 4))) Out.Say("  " + line);
            if (!string.IsNullOrWhiteSpace(item.Url)) Out.Say(new Span("  " + (item.LinkText ?? "More") + ": ", Tone.Dim), new Span(item.Url, Tone.Accent));
            Out.Say();
        }
        if (!everything && items.Count > shown.Count) Out.Hint($"{items.Count - shown.Count} older announcements; '{Prefix}news --all' shows them.");
    }

    private async Task VersionAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var git = _git.IsAvailable ? (await _git.GetVersionAsync())?.ToString(3) : null;
        var runtime = RuntimeInformation.FrameworkDescription;
        var os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()})";
        if (Out.JsonMode)
        {
            Out.Json(new { version = VersionText, git, runtime, os, executable = Environment.ProcessPath, settings = _settings.SettingsPath, logs = DiagnosticLog.FilePath });
            return;
        }
        Out.Say(new Span("basispm ", Tone.Bold), new Span(VersionText, Tone.Accent));
        Out.Table(new List<Span[]>
        {
            new Span[] { new("git", Tone.Dim), git ?? "not found" },
            new Span[] { new(".NET", Tone.Dim), runtime },
            new Span[] { new("OS", Tone.Dim), os },
            new Span[] { new("program", Tone.Dim), Environment.ProcessPath ?? "" },
            new Span[] { new("settings", Tone.Dim), _settings.SettingsPath },
        });
    }

    private Task ClearAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        if (!Console.IsOutputRedirected) Console.Clear();
        return Task.CompletedTask;
    }
}
