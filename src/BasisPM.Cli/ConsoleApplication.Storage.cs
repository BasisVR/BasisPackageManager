using System.Text.Json;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private static readonly string[] CleanVerbs = { "library", "temp", "logs", "clones", "cache" };
    private ProjectStorageService? _storage;

    private ProjectStorageService Storage => _storage ??= new ProjectStorageService(_basisDev, _mounts);

    private async Task StorageAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var install = await LoadInstallAsync();
        ProjectStorageReport report = null!;
        IReadOnlyList<UnusedClone> unused = Array.Empty<UnusedClone>();
        long cacheBytes = 0;
        await RunOperationAsync(async ct =>
        {
            var progress = new Reporter<StorageProgress>(p => { if (Out.IsBusy) Out.Progress($"Measuring {install.DisplayName}{Out.Ellipsis} {p.Files:N0} files, {DiskSpace.Human(p.Bytes)}"); });
            report = await Storage.MeasureAsync(install, progress, ct);
            unused = await Storage.FindUnusedClonesAsync(install, ct);
            cacheBytes = _git.CloneCache is { } cache ? await cache.MeasureBytesAsync(ct) : 0;
        }, $"Measuring {install.DisplayName}" + Out.Ellipsis);
        var settings = await _settings.LoadAsync();
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                project = install.DisplayName,
                path = install.RepoRoot,
                bytes = report.TotalBytes,
                files = report.TotalFiles,
                areas = report.Entries.Where(e => e.Area != StorageArea.DevClone).Select(e => new { area = AreaKey(e.Area), path = e.Path, bytes = e.Bytes, files = e.Files }),
                clones = report.Clones.Select(c => new
                {
                    id = c.Name,
                    path = c.Path,
                    bytes = c.Bytes,
                    used = !unused.Any(u => string.Equals(u.Id, c.Name, StringComparison.OrdinalIgnoreCase)),
                    removable = unused.Any(u => u.Removable && string.Equals(u.Id, c.Name, StringComparison.OrdinalIgnoreCase)),
                }),
                cache = new { path = _git.CloneCache?.Root, bytes = cacheBytes, enabled = settings.UseCloneCache },
            });
            return;
        }
        Out.Heading($"{install.DisplayName}: {DiskSpace.Human(report.TotalBytes)} in {Plural((int)Math.Min(report.TotalFiles, int.MaxValue), "file")}");
        var rows = new List<Span[]>();
        void Add(string label, long bytes, string note, Tone tone = Tone.Dim) => rows.Add(new[] { new Span(label), new Span(DiskSpace.Human(bytes), Tone.Bold), new Span(note, tone) });
        if (report.Find(StorageArea.Library) is { } library) Add("Library", library.Bytes, "Unity's cache; 'clean library' deletes it and Unity rebuilds it");
        if (report.Find(StorageArea.PackageCache) is { } packageCache) Add("  PackageCache", packageCache.Bytes, "part of Library: downloaded packages");
        if (report.Find(StorageArea.Temp) is { } temp) Add("Temp", temp.Bytes, "Unity's temporary files");
        if (report.Find(StorageArea.Logs) is { } logs) Add("Logs", logs.Bytes, "editor and import logs");
        if (report.Find(StorageArea.Obj) is { } obj) Add("obj", obj.Bytes, "build output from your code editor");
        if (report.Find(StorageArea.ServerBuild) is { } server) Add("Basis Server build", server.Bytes, "built server, including its runtime config");
        if (report.Clones.Count > 0) Add(BasisDevStore.FolderName, report.BytesOf(StorageArea.DevClone), Plural(report.Clones.Count, "development clone"));
        Add("Everything else", report.BytesOf(StorageArea.Other), "assets, packages, settings and git history");
        Out.Table(rows);
        if (report.Clones.Count > 0)
        {
            Out.Say();
            Out.Heading("Development clones");
            Out.Table(report.Clones.Select(c =>
            {
                var state = unused.FirstOrDefault(u => string.Equals(u.Id, c.Name, StringComparison.OrdinalIgnoreCase));
                var note = state is null ? new Span("loaded by Unity", Tone.Dim)
                    : state.Removable ? new Span("not used ('clean clones' removes it)", Tone.Warn)
                    : new Span("not used, kept: " + ProjectStorageService.Describe(state.Reason), Tone.Dim);
                return new[] { new Span(c.Name ?? c.Path, Tone.Accent), new Span(DiskSpace.Human(c.Bytes), Tone.Bold), note };
            }).ToList());
        }
        Out.Say();
        Out.Say(new Span("Download cache: ", Tone.Dim), new Span(DiskSpace.Human(cacheBytes), Tone.Bold),
            new Span($" in {_git.CloneCache?.Root}" + (settings.UseCloneCache ? "" : " (turned off)"), Tone.Dim));
        if (!Out.Piped) Out.Hint("'clean library|temp|logs|clones|cache' frees space.");
    }

    private async Task CleanAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 1, 1, "what to clean: library, temp, logs, clones or cache");
        var what = options[0].ToLowerInvariant();
        if (!CleanVerbs.Contains(what))
            throw new UsageException($"'clean {options[0]}' isn't something clean can do." + (Suggest(options[0], CleanVerbs) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : " Use library, temp, logs, clones or cache."));
        if (what == "cache")
        {
            await CleanCacheAsync(assumeYes);
            return;
        }
        var install = await LoadInstallAsync();
        if (what == "clones")
        {
            await CleanClonesAsync(install, assumeYes);
            return;
        }
        var (folder, label, question, clean) = what switch
        {
            "library" => (ProjectStorageService.LibraryPath(install), "Library", "Unity rebuilds it the next time it opens the project, which can take a long time.", (Func<Task<StorageCleanResult>>)(() => ProjectStorageService.CleanLibraryAsync(install))),
            "temp" => (ProjectStorageService.TempPath(install), "Temp", "Unity makes it again when it needs it.", () => ProjectStorageService.ClearTempAsync(install)),
            _ => (ProjectStorageService.LogsPath(install), "Logs", "Log files in use are left.", (Func<Task<StorageCleanResult>>)(() => ProjectStorageService.ClearLogsAsync(install))),
        };
        if (!Directory.Exists(folder))
        {
            Out.Success($"{label} is already empty.");
            return;
        }
        if (what != "logs" && UnityProjectService.IsOpenInUnity(install.UnityProjectPath))
            throw new InvalidOperationException($"Unity has {install.DisplayName} open. Close Unity, then clean {label} again.");
        var size = await Task.Run(() => ProjectStorageService.FolderBytes(folder));
        if (!assumeYes && !Confirm($"Delete {label} ({DiskSpace.Human(size)}) from {install.DisplayName}? {question}"))
        {
            Out.Warning("Cancelled.");
            return;
        }
        StorageCleanResult result = null!;
        await RunOperationAsync(async _ => result = await clean(), $"Cleaning {label}" + Out.Ellipsis);
        Report(result);
    }

    private async Task CleanClonesAsync(BasisInstall install, bool assumeYes)
    {
        IReadOnlyList<UnusedClone> unused = Array.Empty<UnusedClone>();
        await RunOperationAsync(async ct => unused = await Storage.FindUnusedClonesAsync(install, ct), "Looking for unused clones" + Out.Ellipsis);
        foreach (var kept in unused.Where(c => !c.Removable)) Out.Note($"Keeping {kept.Id}: {ProjectStorageService.Describe(kept.Reason)}.");
        var removable = unused.Where(c => c.Removable).ToList();
        if (removable.Count == 0)
        {
            Out.Success("There are no unused clones to remove.");
            return;
        }
        if (!assumeYes && !Confirm($"Remove {Plural(removable.Count, "unused clone")} ({string.Join(", ", removable.Select(c => c.Id))})?"))
        {
            Out.Warning("Cancelled.");
            return;
        }
        StorageCleanResult result = null!;
        await RunOperationAsync(async ct => result = await Storage.RemoveUnusedClonesAsync(install, removable.Select(c => c.Id).ToList(), ct), "Removing unused clones" + Out.Ellipsis);
        Report(result);
    }

    private async Task CleanCacheAsync(bool assumeYes)
    {
        var cache = _git.CloneCache ?? new GitCloneCache();
        var size = await cache.MeasureBytesAsync();
        if (size == 0)
        {
            Out.Success("The download cache is already empty.");
            return;
        }
        if (!assumeYes && !Confirm($"Delete the download cache ({DiskSpace.Human(size)}) in {cache.Root}? New clones download everything again."))
        {
            Out.Warning("Cancelled.");
            return;
        }
        GitCacheClearResult result = null!;
        await RunOperationAsync(async _ => result = await cache.ClearAsync(), "Clearing the download cache" + Out.Ellipsis);
        if (result.Busy > 0)
        {
            Out.Warning($"Freed {DiskSpace.Human(result.Freed)}; {Plural(result.Busy, "repository", "repositories")} in use by a running clone were left.");
            return;
        }
        Out.Success($"Freed {DiskSpace.Human(result.Freed)}.");
    }

    private void Report(StorageCleanResult result)
    {
        if (result.Ok)
        {
            Out.Success(result.Message);
            return;
        }
        Out.Error(result.Message);
        _exitCode = 1;
    }

    private async Task LicensesAsync(List<string> values)
    {
        var options = values.ToList();
        var output = TakeValue(options, "--output");
        var includeUnity = TakeFlag(options, "--include-unity");
        Expect(options, 0, 0, "");
        var install = await LoadInstallAsync();
        LicenseReport report = null!;
        await RunOperationAsync(async ct => report = await LicenseReportService.BuildAsync(install.UnityProjectPath, install.DisplayName, includeUnity, ct), "Reading package licenses" + Out.Ellipsis);
        string? written = null;
        if (output is not null)
        {
            written = FullPath(output);
            if (Path.GetDirectoryName(written) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(written, LicenseReportService.ToMarkdown(report));
        }
        if (Out.JsonMode)
        {
            Out.Json(JsonSerializer.Deserialize<JsonElement>(LicenseReportService.ToJson(report)));
            if (written is not null) Out.Hint($"Saved third-party notices to {written}.");
            return;
        }
        if (report.Packages.Count == 0) Out.Say("This project has no packages to list.");
        else
            Out.Table(report.Packages.Select(p => new[]
            {
                new Span(p.DisplayName),
                new Span(p.Version ?? "", Tone.Dim),
                p.Concern switch
                {
                    LicenseConcern.Missing => new Span("no license stated", Tone.Warn),
                    LicenseConcern.Copyleft => new Span((p.License ?? p.DetectedLicense ?? "") + " (copyleft)", Tone.Warn),
                    _ => new Span(p.License ?? (p.DetectedLicense is { } detected ? detected + " (from its license file)" : ""), Tone.Good),
                },
            }).ToList());
        if (report.Missing.Count > 0) Out.Warning($"{Plural(report.Missing.Count, "package states", "packages state")} no license and {(report.Missing.Count == 1 ? "has" : "have")} no license file: {string.Join(", ", report.Missing.Select(p => p.Id))}. Ask the authors before you ship them.");
        if (report.Copyleft.Count > 0) Out.Warning($"{Plural(report.Copyleft.Count, "package uses", "packages use")} a copyleft license, which can oblige you to share source code: {string.Join(", ", report.Copyleft.Select(p => p.Id))}.");
        if (written is not null) Out.Success($"Saved third-party notices to {written}.");
        else if (report.Packages.Count > 0 && !Out.Piped) Out.Hint($"Add --output <file> to save the notices with each license's full text.");
    }

    private static string AreaKey(StorageArea area) => area switch
    {
        StorageArea.PackageCache => "packageCache",
        StorageArea.ServerBuild => "serverBuild",
        StorageArea.DevClone => "devClone",
        _ => area.ToString().ToLowerInvariant(),
    };

    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
