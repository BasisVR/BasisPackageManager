using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const string UnityHubDownload = "https://unity.com/download";

    private async Task UnityAsync(List<string> values)
    {
        var options = values.ToList();
        var stream = TakeValue(options, "--stream");
        var search = TakeValue(options, "--search");
        var everything = TakeFlag(options, "--all");
        var modules = TakeValues(options, "--module");
        if (options.Count == 0)
        {
            await ListEditorsAsync();
            return;
        }
        switch (options[0].ToLowerInvariant())
        {
            case "releases":
                Expect(options, 1, 1, "");
                await ListUnityReleasesAsync(stream, search, everything);
                break;
            case "install":
                Expect(options, 1, 2, "an editor version");
                await InstallUnityAsync(options.Count == 2 ? options[1] : null, modules);
                break;
            case "modules":
                Expect(options, 2, int.MaxValue, "an editor version");
                await InstallUnityModulesAsync(options[1], options.Skip(2).Concat(modules).ToList());
                break;
            case "add":
                Expect(options, 2, 2, "the editor's folder");
                await AddEditorAsync(options[1]);
                break;
            case "forget" or "remove":
                Expect(options, 2, 2, "an editor version or folder");
                await ForgetEditorAsync(options[1]);
                break;
            case "hub":
                Expect(options, 1, 1, "");
                var settings = await _settings.LoadAsync();
                if (!_unityHub.OpenHub(settings.UnityHubPath)) throw new InvalidOperationException($"Unity Hub wasn't found. Install it from {UnityHubDownload}, or point at it with '{Prefix}config set unity-hub-path <path>'.");
                Out.Success("Opening Unity Hub.");
                break;
            case "open":
                Expect(options, 1, 1, "");
                await OpenUnityAsync(new List<string>());
                break;
            default:
                throw new UsageException($"'unity {options[0]}' isn't something unity can do." + (Suggest(options[0], new[] { "releases", "install", "modules", "add", "forget", "hub", "open" }) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : ""));
        }
    }

    private async Task<List<InstalledEditor>> ListAllEditorsAsync(UserSettings settings)
    {
        IReadOnlyList<InstalledEditor> hubEditors = Array.Empty<InstalledEditor>();
        if (_unityHub.FindHubPath(settings.UnityHubPath) is not null)
            await RunOperationAsync(async ct => hubEditors = await _unityHub.ListInstalledAsync(settings.UnityHubPath, ct), "Asking Unity Hub for its editors" + Out.Ellipsis);
        var editors = hubEditors.ToList();
        foreach (var manual in settings.ManualEditors)
            if (!string.IsNullOrWhiteSpace(manual.Path) && !editors.Any(e => e.Version.Equals(manual.Version, StringComparison.OrdinalIgnoreCase) || Platform.PathsEqual(e.Path, manual.Path)))
                editors.Add(manual.ToInstalledEditor());
        return editors;
    }

    private async Task ListEditorsAsync()
    {
        var settings = await _settings.LoadAsync();
        var hub = _unityHub.FindHubPath(settings.UnityHubPath);
        var editors = await ListAllEditorsAsync(settings);
        var install = await TryLoadInstallAsync();
        var required = install?.UnityVersion is { } v && v != "unknown" ? v : null;
        if (Out.JsonMode)
        {
            Out.Json(new { hub, required, editors = editors.Select(e => new { version = e.Version, path = e.Path, manual = e.IsManual, needed = e.Version == required }) });
            return;
        }
        Out.Say(new Span("Unity Hub  ", Tone.Dim), hub is null ? new Span("not found", Tone.Warn) : new Span(hub));
        if (editors.Count == 0) Out.Say("No Unity editors found.");
        else
        {
            Out.Say();
            Out.Table(editors.OrderByDescending(e => UnityVersion.TryParse(e.Version, out var parsed) ? parsed : null).Select(e => new[]
            {
                new Span(e.Version == required ? "*" : " ", Tone.Good), new Span(e.Version, e.Version == required ? Tone.Good : Tone.Plain),
                new Span(e.Version == required ? $"needed by {install!.DisplayName}" : e.IsManual ? "added by folder" : "", Tone.Accent), new Span(e.Path, Tone.Dim),
            }).ToList(), indent: 0);
        }
        if (required is not null && !editors.Any(e => e.Version == required))
            Out.Warning($"{install!.DisplayName} needs Unity {required}, which isn't installed. '{Prefix}unity install' installs it through Unity Hub.");
        if (hub is null) Out.Hint($"Install Unity Hub from {UnityHubDownload}, or add an editor you already have with '{Prefix}unity add <folder>'.");
    }

    private async Task ListUnityReleasesAsync(string? stream, string? search, bool everything)
    {
        IReadOnlyList<UnityRelease> releases = null!;
        await RunOperationAsync(async ct => releases = await _unityReleases.FetchAllAsync("6000", ct), "Loading Unity releases" + Out.Ellipsis);
        var settings = await _settings.LoadAsync();
        var installed = settings.ManualEditors.Select(m => m.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_unityHub.FindHubPath(settings.UnityHubPath) is not null)
            foreach (var editor in await _unityHub.ListInstalledAsync(settings.UnityHubPath)) installed.Add(editor.Version);
        var required = (await TryLoadInstallAsync())?.UnityVersion;
        var shown = releases.Where(r => stream is null || r.Stream.Equals(stream, StringComparison.OrdinalIgnoreCase))
            .Where(r => search is null || r.Version.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        if (Out.JsonMode)
        {
            Out.Json(shown.Select(r => new { version = r.Version, changeset = r.ShortRevision, stream = r.Stream, recommended = r.Recommended, released = r.ReleaseDate, installed = installed.Contains(r.Version), needed = r.Version == required }));
            return;
        }
        if (shown.Count == 0)
        {
            Out.Say("No releases match.");
            return;
        }
        var limited = everything ? shown : shown.Take(25).ToList();
        Out.Table(limited.Select(r => new[]
        {
            new Span(r.Version, installed.Contains(r.Version) ? Tone.Good : Tone.Plain), new Span(r.Stream, Tone.Dim), new Span(r.ReleaseDate?.ToString("yyyy-MM-dd") ?? "", Tone.Dim),
            new Span(string.Join(", ", new[] { r.Recommended ? "recommended" : null, installed.Contains(r.Version) ? "installed" : null, r.Version == required ? "needed by this project" : null }.Where(s => s is not null)), Tone.Accent),
        }).ToList(), indent: 0);
        if (limited.Count < shown.Count) Out.Hint($"{shown.Count - limited.Count} older releases hidden; add --all to see them.");
        if (!Out.Piped) Out.Hint($"Install one with '{Prefix}unity install <version>'.");
    }

    private async Task InstallUnityAsync(string? version, List<string> modules)
    {
        var settings = await _settings.LoadAsync();
        var wanted = version ?? (await LoadInstallAsync()).UnityVersion;
        if (string.IsNullOrWhiteSpace(wanted) || wanted == "unknown") throw new InvalidOperationException("This project's Unity version is unknown, so name the version to install.");
        if (_unityHub.FindHubPath(settings.UnityHubPath) is null)
            throw new InvalidOperationException($"Unity Hub wasn't found. Install it from {UnityHubDownload}, or point at it with '{Prefix}config set unity-hub-path <path>'.");
        var editors = await ListAllEditorsAsync(settings);
        if (editors.Any(e => e.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
        {
            Out.Success($"Unity {wanted} is already installed.");
            if (modules.Count > 0) Out.Hint($"Add modules to it with '{Prefix}unity modules {wanted} {string.Join(" ", modules)}'.");
            return;
        }
        IReadOnlyList<UnityRelease> releases = null!;
        await RunOperationAsync(async ct => releases = await _unityReleases.FetchAllAsync("6000", ct), "Looking up Unity " + wanted + Out.Ellipsis);
        var release = releases.FirstOrDefault(r => r.Version.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unity {wanted} isn't in the list of Unity 6 releases, so its changeset is unknown. Install it from Unity Hub, or see https://unity.com/releases/editor/archive.");
        var code = 0;
        await RunOperationAsync(async ct => code = await _unityHub.InstallEditorAsync(release.Version, release.ShortRevision, modules, settings.UnityHubPath, ct),
            $"Installing Unity {release.Version} through Unity Hub (this takes a while)" + Out.Ellipsis);
        if (code != 0) throw new InvalidOperationException($"Unity Hub couldn't install Unity {release.Version} (exit code {code}). Try it from Unity Hub to see why.");
        Out.Success($"Unity Hub installed Unity {release.Version}{(modules.Count > 0 ? " with " + string.Join(", ", modules) : "")}.");
    }

    private async Task InstallUnityModulesAsync(string version, List<string> modules)
    {
        if (modules.Count == 0) throw new UsageException($"Name at least one module, such as {string.Join(", ", UnityModules.Take(3))}.");
        var settings = await _settings.LoadAsync();
        var editors = await ListAllEditorsAsync(settings);
        var editor = editors.FirstOrDefault(e => e.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Unity {version} isn't installed. '{Prefix}unity' lists the editors you have.");
        if (editor.IsManual) throw new InvalidOperationException($"Unity {version} was added by folder, and Unity Hub can only add modules to editors it installed.");
        var code = 0;
        await RunOperationAsync(async ct => code = await _unityHub.InstallModulesAsync(editor.Version, modules, settings.UnityHubPath, ct), $"Adding {string.Join(", ", modules)} to Unity {editor.Version}" + Out.Ellipsis);
        if (code != 0) throw new InvalidOperationException($"Unity Hub couldn't add the modules (exit code {code}).");
        Out.Success($"Added {string.Join(", ", modules)} to Unity {editor.Version}.");
    }

    private async Task AddEditorAsync(string folder)
    {
        var full = FullPath(folder);
        if (!new UnityEditorLocator().TryResolve(full, out var editor))
            throw new InvalidOperationException($"Couldn't find a Unity editor in {full}. Pick the version-named editor folder (the one containing the Editor app).");
        var settings = await _settings.LoadAsync();
        if (settings.ManualEditors.Any(m => Platform.PathsEqual(m.Path, editor.Path)))
        {
            Out.Success($"Unity {editor.Version} is already on the list.");
            return;
        }
        await _settings.UpdateAsync(s => s.ManualEditors.Add(new ManualUnityEditor { Version = editor.Version, Path = editor.Path }));
        Out.Success($"Added Unity {editor.Version} ({editor.Path}).");
    }

    private async Task ForgetEditorAsync(string value)
    {
        var settings = await _settings.LoadAsync();
        var full = Directory.Exists(FullPath(value)) || File.Exists(FullPath(value)) ? FullPath(value) : null;
        bool Matches(ManualUnityEditor m) => m.Version.Equals(value, StringComparison.OrdinalIgnoreCase) || full is not null && (Platform.PathsEqual(m.Path, full) || m.Path.StartsWith(full + Path.DirectorySeparatorChar, Platform.PathComparison()));
        var matches = settings.ManualEditors.Where(Matches).ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"No editor added by folder matches '{value}'. Editors installed by Unity Hub are removed from Unity Hub itself.");
        await _settings.UpdateAsync(s => s.ManualEditors.RemoveAll(Matches));
        Out.Success($"Took {string.Join(", ", matches.Select(m => "Unity " + m.Version))} off the list. The editor files weren't touched.");
    }

    private async Task OpenUnityAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        if (!install.HasUnityProject) throw new InvalidOperationException($"{install.DisplayName} has no Unity project to open.");
        var settings = await _settings.LoadAsync();
        var extraEditors = settings.ManualEditors.Select(x => x.ToInstalledEditor());
        var opened = false;
        await RunOperationAsync(async ct => opened = await _unityHub.OpenProjectAsync(install.UnityProjectPath, install.UnityVersion, settings.UnityHubPath, extraEditors, ct), "Starting Unity" + Out.Ellipsis);
        if (opened)
        {
            Out.Success($"Opening {install.DisplayName} in Unity {install.UnityVersion}.");
            return;
        }
        _exitCode = 1;
        if (_unityHub.OpenHub(settings.UnityHubPath)) Out.Warning($"Unity {install.UnityVersion} isn't installed, so Unity Hub was opened instead. '{Prefix}unity install' installs it.");
        else Out.Error($"Unity {install.UnityVersion} isn't installed and Unity Hub wasn't found. Install Unity Hub from {UnityHubDownload}, or add an editor with '{Prefix}unity add <folder>'.");
    }
}
