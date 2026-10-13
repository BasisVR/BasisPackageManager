using System.Text.Json;
using System.Text.Json.Serialization;
using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private async Task ListPackagesAsync(List<string> values)
    {
        var options = values.ToList();
        var builtIn = TakeFlag(options, "--built-in") | TakeFlag(options, "--builtin");
        var installedOnly = TakeFlag(options, "--installed");
        Expect(options, 0, 1, "a search term");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var sources = _basisDev.Scan(install);
        var shipped = await LoadShippedPackagesAsync(install);
        var search = options.Count == 1 ? options[0] : null;
        var latest = _catalogs.AllLatest(catalog).ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var mounts = WorkingCloneMounts(install);
        var packages = latest.Values.Select(p => (p.Name, p.DisplayName, p.Category, p.Source, Version: (string?)p.Version))
            .Concat(shipped.LocalIds(catalog).Select(id => (Name: id, DisplayName: shipped.Local(id)?.DisplayName ?? id, Category: (string?)null, Source: (string?)null, Version: shipped.Local(id)?.Version)))
            .Where(p => shipped.IsBuiltIn(p.Name, p.Source) == builtIn)
            .Where(p => string.IsNullOrWhiteSpace(search)
                || p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || p.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (p.Category?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(p => new PackageItem(p.Name, p.DisplayName, p.Category, p.Source, p.Version, PackageState(p.Name, shipped, sources), InstalledLabel(install, p.Name, shipped, mounts)))
            .Where(p => !installedOnly || p.State != "available")
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (Out.JsonMode)
        {
            Out.Json(packages);
            return;
        }
        if (packages.Count == 0)
        {
            Out.Say(search is null ? installedOnly ? "No packages are installed on top of Basis." : "No packages to show." : $"Nothing matches '{search}'.");
            if (!builtIn) Out.Hint($"Packages that ship with Basis are listed with '{Prefix}list-packages --built-in'.");
            return;
        }
        Out.Table(packages.Select(p => new[]
        {
            new Span(p.Id, p.State == "available" ? Tone.Plain : Tone.Good), new Span(p.Name),
            new Span(p.Installed ?? (p.State == "available" ? p.Latest ?? "" : ""), Tone.Dim), new Span(p.State, p.State == "available" ? Tone.Dim : Tone.Accent),
        }).ToList(), indent: 0);
        if (!Out.Piped) Out.Hint(builtIn
            ? $"{Plural(packages.Count, "package")} ship with Basis."
            : $"{Plural(packages.Count, "package")}. '{Prefix}info <id>' shows details, '{Prefix}install-package <id>' installs one; '--built-in' lists the ones that ship with Basis.");
    }

    private sealed record PackageItem(string Id, string Name, string? Category, string? Source, string? Latest, string State, string? Installed);

    private static string PackageState(string id, ShippedPackages shipped, BasisDevReport sources) => shipped.IsIncluded(id) ? "included" : sources.Find(id)?.Source switch
    {
        PackageSourceKind.ProjectRepo => "included",
        PackageSourceKind.LocalFolder => "local folder",
        PackageSourceKind.DevClone => "dev clone",
        PackageSourceKind.None or null => "available",
        _ => "installed",
    };

    private string? InstalledLabel(BasisInstall install, string id, ShippedPackages shipped, IReadOnlyDictionary<string, MountRecord>? mounts = null)
    {
        if (shipped.Local(id) is { } local && !shipped.Mounted.Contains(id)) return string.IsNullOrWhiteSpace(local.Version) ? null : local.Version;
        var mount = mounts is null ? WorkingCloneMount(install, id) : mounts.GetValueOrDefault(id);
        var value = mount?.OriginalManifestValue ?? install.Manifest.Dependencies.GetValueOrDefault(id);
        if (string.IsNullOrEmpty(value) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
        return UpmGitUrl.Parse(value) is { } git ? git.Ref ?? "default branch" : value;
    }

    private async Task PackageInfoAsync(List<string> values)
    {
        Expect(values, 1, 1, "a package id");
        var id = values[0];
        var catalog = await LoadCatalogAsync();
        var entry = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        var install = await TryLoadInstallAsync();
        ShippedPackages? shipped = install is null ? null : await LoadShippedPackagesAsync(install);
        var local = shipped?.Local(id);
        var manifestValue = install?.Manifest.Dependencies.FirstOrDefault(d => d.Key.Equals(id, StringComparison.OrdinalIgnoreCase)).Value;
        var mount = install is null ? null : WorkingCloneMount(install, id);
        if (entry is null && local is null && manifestValue is null && mount is null)
            throw new InvalidOperationException(NotFound(id, catalog, install));
        var packageId = entry?.Name ?? local?.Id ?? id;
        var hasEdits = mount is not null && await _mounts.HasLocalWorkAsync(mount.FolderPath);
        var state = install is null ? null : shipped!.IsIncluded(packageId) ? "yes, it ships with Basis"
            : mount is not null ? $"yes, as an editable working copy in {RelativeTo(install.UnityProjectPath, mount.FolderPath)}" + (hasEdits ? " (has local edits)" : "")
            : local is not null ? $"yes, as the local folder {RelativeTo(install.UnityProjectPath, local.FolderPath)}"
            : manifestValue is not null ? $"yes, manifest.json has {manifestValue}" : "no";
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                id = packageId, name = entry?.DisplayName ?? local?.DisplayName, version = entry?.Version ?? local?.Version, description = entry?.Description ?? local?.Description,
                author = entry?.Author?.Name, license = entry?.License, unity = entry?.Unity, category = entry?.Category, source = entry?.Source, tags = entry?.Tags, url = entry?.Url, link = entry?.Link,
                stars = entry?.Stars, forks = entry?.Forks, updated = entry?.Updated, server = entry?.Server, dependencies = entry?.Dependencies,
                project = install is null ? null : new { name = install.DisplayName, state, installed = InstalledLabel(install, packageId, shipped!), manifest = manifestValue, workingCopy = mount?.FolderPath, localEdits = hasEdits },
            });
            return;
        }
        Out.Say(new Span(entry?.DisplayName ?? local?.DisplayName ?? packageId, Tone.Bold), new Span("  " + packageId, Tone.Dim));
        var description = entry?.Description ?? local?.Description;
        if (!string.IsNullOrWhiteSpace(description)) foreach (var line in Out.Wrap(description, Math.Max(40, Out.Width - 2))) Out.Say(line);
        Out.Say();
        var rows = new List<Span[]>();
        void Row(string label, string? value, Tone tone = Tone.Plain) { if (!string.IsNullOrWhiteSpace(value)) rows.Add(new[] { new Span(label, Tone.Dim), new Span(value, tone) }); }
        Row("Version", entry?.Version ?? local?.Version);
        Row("Author", entry?.Author is { } author ? author.Name + (string.IsNullOrWhiteSpace(author.Url) ? "" : $" ({author.Url})") : null);
        Row("License", entry?.License);
        Row("Unity", entry?.Unity);
        Row("Category", string.Join(", ", new[] { entry?.Category, entry?.Source }.Where(s => !string.IsNullOrWhiteSpace(s))));
        Row("Tags", entry?.Tags is { Count: > 0 } tags ? string.Join(", ", tags) : null);
        Row("Repository", entry?.Url);
        Row("Link", entry?.Link);
        Row("Popularity", entry is { Stars: > 0 } || entry is { Forks: > 0 } ? $"{entry.Stars} stars, {entry.Forks} forks" + (entry.Updated is { Length: >= 10 } updated ? $", updated {updated[..10]}" : "") : null);
        Row("Depends on", entry?.Dependencies is { Count: > 0 } deps ? string.Join(", ", deps.Select(d => $"{d.Key} {d.Value}")) : null);
        if (install is not null)
        {
            var root = MountedPackageRoot(install, packageId) ?? local?.FolderPath;
            var gaps = root is null ? Array.Empty<PackageDependency>() : PackageDependencies.AnalyzeInstalled(install.UnityProjectPath, root, install.Manifest, catalog);
            Row("Missing", gaps.Any(g => g.State != DependencyState.Present && g.State != DependencyState.UnityResolves)
                ? string.Join(", ", gaps.Where(g => g.State != DependencyState.Present && g.State != DependencyState.UnityResolves).Select(g => g.Id)) : null, Tone.Warn);
            Row("Needed by", PackageDependencies.FindDependents(install.UnityProjectPath, packageId, catalog) is { Count: > 0 } dependents ? string.Join(", ", dependents.Select(d => d.Id)) : null);
        }
        Row("Server", entry?.Server == true ? "also adds a Basis Server side, installed with it" : null);
        if (install is not null) Row("Installed", state, state == "no" ? Tone.Dim : Tone.Good);
        Out.Table(rows, indent: 0);
        if (install is not null && state == "no") Out.Hint($"Install it with '{Prefix}install-package {packageId}'; '{Prefix}versions {packageId}' lists the releases.");
    }

    private string NotFound(string id, Catalog catalog, BasisInstall? install)
    {
        var names = catalog.Packages.Keys.Concat(install?.Manifest.Dependencies.Keys ?? Enumerable.Empty<string>());
        var suggestions = Suggest(id, names);
        return $"There's no package '{id}' in the registry or this project." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : $" '{Prefix}list-packages' shows what's available.");
    }

    private async Task<BasisInstall?> TryLoadInstallAsync()
    {
        try { return await LoadInstallAsync(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            if (_commandProject is not null) throw;
            DiagnosticLog.Write("Loading the project for package details", ex);
            return null;
        }
    }

    private async Task PackageVersionsAsync(List<string> values)
    {
        Expect(values, 1, 1, "a package id");
        var id = values[0];
        var catalog = await LoadCatalogAsync();
        var entry = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
        var install = await TryLoadInstallAsync();
        var current = install is null ? null : WorkingCloneMount(install, id)?.OriginalManifestValue ?? install.Manifest.Dependencies.GetValueOrDefault(id);
        var url = entry?.Url ?? (UpmGitUrl.Parse(current) is not null ? current : null);
        if (url is null)
        {
            if (entry is null && current is null) throw new InvalidOperationException(NotFound(id, catalog, install));
            throw new InvalidOperationException($"{entry?.DisplayName ?? id} has no git repository, so there are no versions to pick from.");
        }
        PackageVersions versions = null!;
        var token = await TryGitHubTokenAsync();
        await RunOperationAsync(async ct => versions = await _versions.GetVersionsAsync(url, token, ct), "Listing releases" + Out.Ellipsis);
        var installedRef = UpmGitUrl.Parse(current)?.Ref;
        var latestStable = versions.LatestStable?.Ref;
        if (Out.JsonMode)
        {
            Out.Json(versions.Options.Select(v => new { @ref = v.Ref, label = v.Label, kind = v.Kind, prerelease = v.IsPrerelease, latest = v.Ref is not null && v.Ref == latestStable, installed = current is not null && v.Ref == installedRef }));
            return;
        }
        if (versions.Options.Count <= 1) Out.Say($"{entry?.DisplayName ?? id} has no releases or tags yet, only its default branch.");
        Out.Table(versions.Options.Select(v => new[]
        {
            new Span(v.Ref ?? "default", v.Ref == installedRef && current is not null ? Tone.Good : Tone.Plain),
            new Span(v.Kind switch { VersionKind.Release => "release", VersionKind.Tag => "tag", _ => "branch" }, Tone.Dim),
            new Span(string.Join(", ", new[] { v.IsPrerelease ? "prerelease" : null, v.Ref is not null && v.Ref == latestStable ? "latest" : null, current is not null && v.Ref == installedRef ? "installed" : null }.Where(s => s is not null)), Tone.Accent),
            new Span(v.Label == (v.Ref ?? "") ? "" : v.Label, Tone.Dim),
        }).ToList(), indent: 0);
        if (!Out.Piped) Out.Hint($"Install one with '{Prefix}install-package {entry?.Name ?? id} --version <ref>'.");
    }

    private async Task InstallPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var version = TakeValue(options, "--version");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var noDeps = TakeFlag(options, "--no-deps");
        Expect(options, 1, int.MaxValue, "a package id");
        if (version is not null && options.Count > 1) throw new UsageException("--version works with one package at a time.");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var latest = _catalogs.AllLatest(catalog).ToList();
        var shipped = await LoadShippedPackagesAsync(install);
        var failed = 0;
        foreach (var id in options)
        {
            var entry = latest.FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                if (options.Count == 1) throw new InvalidOperationException(NotFound(id, catalog, install));
                Out.Error(NotFound(id, catalog, install));
                failed++;
                continue;
            }
            try
            {
                if (await InstallCatalogPackageAsync(install, catalog, entry, version, assumeYes, shipped) && !noDeps)
                    await InstallMissingDependenciesAsync(install, catalog, entry.Name, assumeYes, shipped, 0);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException && options.Count > 1)
            {
                DiagnosticLog.Write($"Installing {entry.Name}", ex);
                Out.Error($"{entry.DisplayName}: {ex.Message}");
                failed++;
            }
        }
        if (failed > 0) _exitCode = 1;
    }

    private async Task<bool> InstallCatalogPackageAsync(BasisInstall install, Catalog catalog, CatalogPackageVersion entry, string? version, bool assumeYes, ShippedPackages shipped)
    {
        await RefuseAdvisedAsync(entry.Name, entry.Url, entry.DisplayName);
        if (shipped.IsIncluded(entry.Name))
        {
            Out.Success($"{entry.DisplayName} ships with Basis, so it's already in {install.DisplayName}.");
            return false;
        }
        if (shipped.Local(entry.Name) is { } local && !shipped.Mounted.Contains(entry.Name))
            throw new InvalidOperationException($"{entry.DisplayName} is already in {RelativeTo(install.UnityProjectPath, local.FolderPath)} as a local folder. Move that folder away first if you want the registry version.");
        var existing = WorkingCloneMount(install, entry.Name);

        await ReloadManifestAsync(install);
        var current = existing?.OriginalManifestValue ?? install.Manifest.Dependencies.FirstOrDefault(d => d.Key.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)).Value;
        var requested = new List<(string, string)>();
        if (SemVer.TryParse(entry.Version, out _)) requested.Add((entry.Name, $"^{entry.Version}"));
        foreach (var (name, range) in install.Manifest.Dependencies)
            if (!name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(range) && SemVerRange.TryParse(range, out _)) requested.Add((name, range));
        var resolution = new DependencyResolver(_catalogs).Resolve(catalog, requested);
        if (resolution.Conflicts.Count > 0) throw new InvalidOperationException("Dependency conflict: " + string.Join("; ", resolution.Conflicts));
        if (!resolution.Resolved.TryGetValue(entry.Name, out var main) && string.IsNullOrWhiteSpace(entry.Url) && !SemVer.TryParse(entry.Version, out _))
            throw new InvalidOperationException($"{entry.DisplayName} has neither a version nor a repository to install from.");
        var (url, latest) = await InstallUrlAsync(entry, main, version, current);
        var added = 0;
        foreach (var (name, resolved) in resolution.Resolved)
        {
            if (name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) || HasDependency(install, name) || IsPresentInSource(install, shipped, name) || shipped.IsIncluded(name)) continue;
            await RefuseAdvisedAsync(name, resolved.Url, resolved.DisplayName);
            install.Manifest.Dependencies[name] = PackageTrust.PinnedUrl(resolved) ?? resolved.Version;
            added++;
        }
        if (added > 0) await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);

        var dependencyNote = added > 0 ? $" with {Plural(added, "dependency", "dependencies")}" : "";
        if (string.IsNullOrWhiteSpace(url))
        {
            install.Manifest.Dependencies[entry.Name] = main?.Version ?? entry.Version;
            await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
            Out.Success($"Installed {entry.DisplayName} {main?.Version ?? entry.Version}{dependencyNote} into {install.DisplayName}.");
        }
        else
        {
            var registry = !_unofficialIds.Contains(entry.Name);
            var review = ReviewGate($"{entry.DisplayName} comes from a catalog you added, not the Basis registry, so nobody has checked it for you.", main ?? entry, registry, assumeYes);
            bool cloned;
            string mountedUrl;
            try { (cloned, mountedUrl) = await CloneInstallAsync(install, entry.Name, url, latest, review, reviewRequired: !registry, assumeYes: assumeYes); }
            catch (DeclinedException)
            {
                Out.Warning($"Left {entry.DisplayName} uninstalled.");
                return false;
            }
            if (mountedUrl != url) Out.Warning($"{UpmGitUrl.Parse(url)?.Ref} of {entry.DisplayName} isn't a Unity package (it has no package.json), so its default branch was installed instead.");
            url = mountedUrl;
            var at = UpmGitUrl.Parse(url)?.Ref is { } gitRef ? $" {gitRef}" : " (default branch)";
            Out.Success(cloned
                ? $"Installed {entry.DisplayName}{at}{dependencyNote} into {install.DisplayName} as an editable working copy."
                : $"Installed {entry.DisplayName}{at}{dependencyNote} into {install.DisplayName}.");
        }
        if (await SyncServerSideAsync(install, entry, url ?? "") is { } problem) Out.Warning($"The Unity side is installed, but the Basis Server side was not: {problem}");
        else if (entry.Server && ServerPackageService.SupportsPackages(install.RepoRoot)) Out.Success($"Added its Basis Server side too. Run '{Prefix}server-build' to compile it in.");
        return true;
    }

    private async Task<(string? Url, bool Latest)> InstallUrlAsync(CatalogPackageVersion entry, CatalogPackageVersion? main, string? version, string? current)
    {
        if (version is not null)
        {
            var location = UpmGitUrl.Parse(entry.Url) ?? throw new InvalidOperationException($"{entry.DisplayName} has no git repository, so --version can't pick a release.");
            var unpinned = location.ToManifestUrl(null, location.Path);
            return version.ToLowerInvariant() switch
            {
                "latest" => (await LatestReleaseUrlAsync(unpinned) ?? throw new InvalidOperationException($"Couldn't find a release of {entry.DisplayName}. Use --version default for its default branch, or name a tag."), true),
                "default" or "head" => (unpinned, false),
                _ when GitUrlPolicy.IsSafeRef(version) => (location.ToManifestUrl(version, location.Path), false),
                _ => throw new UsageException($"'{version}' isn't a valid git ref."),
            };
        }
        if (main?.Url is not { } mainUrl) return (entry.Url, false);
        if (await LatestReleaseUrlAsync(mainUrl) is { } latest) return (latest, true);
        if (UpmGitUrl.Parse(mainUrl)?.Ref is null && UpmGitUrl.Parse(current)?.Ref is { } pinned)
            throw new InvalidOperationException($"Couldn't find {entry.DisplayName}'s newest release, so it stays at {pinned}. Pass --version <ref> to pick one, or --version default for its default branch.");
        return (mainUrl, false);
    }

    private async Task<bool> HasUnsavedWorkAsync(MountRecord mount, CancellationToken ct = default)
    {
        try { return await _mounts.HasLocalWorkAsync(mount.FolderPath, ct); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            DiagnosticLog.Write($"Checking {mount.FolderPath} for local work", ex);
            return true;
        }
    }

    private static bool HasDependency(BasisInstall install, string id) => install.Manifest.Dependencies.Keys.Any(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));

    private async Task<(bool Cloned, string Url)> CloneInstallAsync(BasisInstall install, string packageId, string gitUrl, bool orDefaultBranch, Func<PackageReview, Task<bool>>? review = null, bool reviewRequired = false, bool assumeYes = false)
    {
        if (_git.IsAvailable && WorkingCloneMount(install, packageId) is not null && UpmGitUrl.Parse(gitUrl) is { } parsed)
        {
            var approve = review is null ? null : PackageUpdates.ReviewBeforeUpdate(review);
            var request = new PackageUpdateRequest(parsed.Ref, parsed.CloneUrl);
            PackageUpdateResult updated = null!;
            await RunOperationAsync(async ct => updated = await PackageUpdates.UpdateCloneAsync(install, packageId, request, approve, Out.Progress, ct), $"Updating {packageId}" + Out.Ellipsis);
            if (updated.Outcome == PackageUpdateOutcome.NotAPackage && orDefaultBranch && parsed.Ref is not null)
                await RunOperationAsync(async ct => updated = await PackageUpdates.UpdateCloneAsync(install, packageId, request with { Ref = null }, approve, Out.Progress, ct), $"Updating {packageId}" + Out.Ellipsis);
            switch (updated.Outcome)
            {
                case PackageUpdateOutcome.Updated or PackageUpdateOutcome.UpToDate:
                    return (true, updated.ManifestValue ?? gitUrl);
                case PackageUpdateOutcome.Declined:
                    throw new DeclinedException();
                case PackageUpdateOutcome.Conflict:
                    if (!assumeYes && !Confirm($"{updated.Message} Replace your copy with a clean one? Your edits will be lost."))
                        throw new InvalidOperationException(updated.Message);
                    break;
                case PackageUpdateOutcome.NotAClone:
                    break;
                default:
                    throw new InvalidOperationException(updated.Message);
            }
        }
        if (WorkingCloneMount(install, packageId) is not null)
        {
            var swap = await _mounts.UnmountAsync(install, packageId, restoreManifest: true, restoreLine: gitUrl);
            if (!swap.Ok) throw new InvalidOperationException(swap.Error ?? "Couldn't remove the old working copy. Close Unity if it has the package open, then try again.");
        }
        if (_git.IsAvailable && UpmGitUrl.Parse(gitUrl) is not null)
        {
            MountResult result = null!;
            await RunOperationAsync(async ct => result = await _mounts.MountAsync(install, packageId, gitUrl, Out.Progress, ct, orDefaultBranch, review), $"Cloning {packageId}" + Out.Ellipsis);
            if (result.Ok) return (true, result.ManifestValue ?? gitUrl);
            if (result.Declined) throw new DeclinedException();
            if (result.NotAPackage || reviewRequired) throw new InvalidOperationException(result.Error);
            Out.Warning($"Couldn't clone {packageId} ({result.Error}), so it was added to manifest.json for Unity to download instead.");
        }
        else if (reviewRequired) throw new InvalidOperationException($"Git was not found on PATH, so {packageId} can't be downloaded and checked before Unity loads it. Install Git, then try again.");
        await ReloadManifestAsync(install);
        install.Manifest.Dependencies[packageId] = gitUrl;
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        return (false, gitUrl);
    }

    private async Task RemovePackageAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var force = TakeFlag(options, "--force");
        Expect(options, 1, int.MaxValue, "a package id");
        var install = await LoadInstallAsync();
        var shipped = await LoadShippedPackagesAsync(install);
        var failed = 0;
        foreach (var id in options)
        {
            try { await RemoveOnePackageAsync(install, id, assumeYes, force, shipped); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException && options.Count > 1)
            {
                DiagnosticLog.Write($"Removing {id}", ex);
                Out.Error($"{id}: {ex.Message}");
                failed++;
            }
        }
        if (failed > 0) _exitCode = 1;
    }

    private async Task RemoveOnePackageAsync(BasisInstall install, string id, bool assumeYes, bool force, ShippedPackages shipped)
    {
        var line = install.Manifest.Dependencies.Keys.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
        id = line ?? shipped.Mounted.FirstOrDefault(m => m.Equals(id, StringComparison.OrdinalIgnoreCase)) ?? id;
        if (shipped.IsIncluded(id)) throw new InvalidOperationException($"{id} ships with Basis, so it stays. ('{Prefix}basisdev' manages development clones of Basis packages.)");
        if (shipped.Local(id) is { } local && !shipped.Mounted.Contains(id))
            throw new InvalidOperationException($"{id} is the folder {RelativeTo(install.UnityProjectPath, local.FolderPath)} inside the project, not something installed. Delete that folder yourself if you want it gone.");
        var mount = WorkingCloneMount(install, id);
        if (!ConfirmDependents(install, id, force))
        {
            Out.Warning($"Kept {id}.");
            return;
        }
        if (!shipped.Known && mount is null && line is not null && !force)
            throw new InvalidOperationException($"This project's git history can't show which packages Basis itself needs, so removing {id} from manifest.json could break it. Add --force if you're sure it's one you added.");
        var mountedRoot = MountedPackageRoot(install, id);
        if (mount is not null && await HasUnsavedWorkAsync(mount)
            && !assumeYes && !Confirm($"{id} has local edits or commits in {RelativeTo(install.UnityProjectPath, mount.FolderPath)} that would be lost. Remove it anyway?"))
        {
            Out.Warning($"Kept {id}.");
            return;
        }
        await ReloadManifestAsync(install);
        var key = install.Manifest.Dependencies.Keys.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (mount is null && key is null && BasisPackagesStore.Find(install.UnityProjectPath, id) is null && BasisDevStore.Read(install.UnityProjectPath, id) is null)
        {
            var suggestions = Suggest(id, install.Manifest.Dependencies.Keys);
            throw new InvalidOperationException($"{id} isn't installed in {install.DisplayName}." + (suggestions.Count > 0 ? $" Did you mean {JoinOr(suggestions.Select(s => $"'{s}'"))}?" : ""));
        }
        var removed = await _mounts.RemoveAsync(install, key ?? id);
        if (!removed.Ok) throw new InvalidOperationException(removed.Error ?? "Couldn't delete the working copy. Close Unity if it has the package open, then try again.");
        var serverProblem = await RemoveServerSideAsync(install, key ?? id, mountedRoot);
        Out.Success($"Removed {key ?? id} from {install.DisplayName}.");
        if (serverProblem is not null) Out.Warning($"Its Basis Server side couldn't be removed: {serverProblem}");
    }

    private async Task AddGitAsync(List<string> values)
    {
        var options = values.ToList();
        var force = TakeFlag(options, "--force");
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var noDeps = TakeFlag(options, "--no-deps");
        Expect(options, 1, 1, "a GitHub repository (URL or owner/repo)");
        GitHubLocator location;
        try { location = GitHubService.Parse(options[0]); }
        catch (FormatException ex) { throw new InvalidOperationException($"'{options[0]}' isn't a GitHub repository: {ex.Message}"); }
        var install = await LoadInstallAsync();
        UpmPackageJson? package = null;
        await RunOperationAsync(async ct => package = await _github.FetchPackageJsonAsync(location, ct), $"Reading package.json from {location.Owner}/{location.Repo}" + Out.Ellipsis);
        if (package is null || string.IsNullOrWhiteSpace(package.Name))
            throw new InvalidOperationException($"Couldn't read a package.json at {location.Owner}/{location.Repo}{(location.Path is null ? "" : "/" + location.Path)}{(location.Branch is null ? "" : " (" + location.Branch + ")")}. Check the folder with ?path= and the ref with #.");
        var manifestUrl = GitHubService.BuildManifestUrl(location);
        if (!GitUrlPolicy.IsSafeDependencyUrl(manifestUrl)) throw new InvalidOperationException($"Refusing the unsupported git URL {manifestUrl}.");
        var shipped = await LoadShippedPackagesAsync(install);
        await ReloadManifestAsync(install);
        var id = install.Manifest.Dependencies.Keys.FirstOrDefault(k => k.Equals(package.Name, StringComparison.OrdinalIgnoreCase)) ?? package.Name;
        if (shipped.IsIncluded(id) && !force)
            throw new InvalidOperationException($"{id} ships with Basis; replacing its manifest.json line could break the project. Add --force if you mean it.");
        if (!shipped.Known && HasDependency(install, id) && !force)
            throw new InvalidOperationException($"manifest.json already has {id}, and this project's git history can't show whether Basis itself needs that line. Add --force to replace it.");
        if (WorkingCloneMount(install, id) is { } mount)
            throw new InvalidOperationException($"{id} is installed as an editable working copy in {RelativeTo(install.UnityProjectPath, mount.FolderPath)}. Remove it first with '{Prefix}remove-package {id}'.");
        var existed = HasDependency(install, id);
        var label = string.IsNullOrWhiteSpace(package.DisplayName) ? package.Name : package.DisplayName;
        if (!BasisDevStore.IsValidId(id)) throw new InvalidOperationException($"\"{id}\" isn't a valid package name.");
        await RefuseAdvisedAsync(id, manifestUrl, label);
        var catalog = await LoadCatalogAsync();
        var listed = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(id, StringComparison.OrdinalIgnoreCase) && p.Url is { Length: > 0 } url && AdvisoryService.SameRepository(url, manifestUrl));
        var registry = listed is not null && !_unofficialIds.Contains(listed.Name);
        try { await CloneInstallAsync(install, id, manifestUrl, false, ReviewGate($"{label} isn't in the Basis registry, so nobody has checked it for you.", listed, registry, assumeYes), reviewRequired: !registry); }
        catch (DeclinedException)
        {
            Out.Warning($"Left {label} uninstalled.");
            return;
        }
        await ReloadManifestAsync(install);
        var added = 0;
        if (package.Dependencies is { Count: > 0 } dependencies)
        {
            added = AddCatalogDependencies(install, catalog, dependencies.Select(d => (d.Key, SemVer.TryParse(d.Value, out _) ? ">=" + d.Value.Trim() : d.Value)), shipped);
            if (added > 0) await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        }
        Out.Success($"{(existed ? "Updated" : "Added")} {label} ({manifestUrl}) as an editable working copy{(added > 0 ? $" and {Plural(added, "registry dependency", "registry dependencies")}" : "")}.");
        if (!noDeps) await InstallMissingDependenciesAsync(install, catalog, id, assumeYes, shipped, 0);
    }

    private int AddCatalogDependencies(BasisInstall install, Catalog catalog, IEnumerable<(string Name, string Range)> requested, ShippedPackages shipped)
    {
        var result = new DependencyResolver(_catalogs).Resolve(catalog, requested);
        var added = 0;
        foreach (var (name, version) in result.Resolved)
        {
            if (HasDependency(install, name) || IsPresentInSource(install, shipped, name) || shipped.IsIncluded(name)) continue;
            if (AdvisoryService.Match(_advisoryList, name, version.Url, null, unknownCommitMatches: false) is { Blocks: true })
            {
                Out.Warning($"Skipped {name}: it was pulled from the Basis registry.");
                continue;
            }
            install.Manifest.Dependencies[name] = PackageTrust.PinnedUrl(version) ?? version.Version;
            added++;
        }
        return added;
    }

    private async Task ListPackageListsAsync(List<string> values)
    {
        Expect(values, 0, 0, "");
        var settings = await _settings.LoadAsync();
        List<PackageList> lists = null!;
        await RunOperationAsync(async ct => lists = await _packageLists.TryLoadAsync(settings.CatalogUrl, ct) ?? throw new InvalidOperationException("Couldn't reach the package registry to load its package lists. Check your connection, then try again."), "Loading package lists" + Out.Ellipsis);
        if (Out.JsonMode)
        {
            Out.Json(lists.Select(l => new { id = l.Id, name = l.Name, description = l.Description, author = l.Author, basisBranch = l.BasisBranch, unity = l.Unity, packages = l.Packages.Select(p => p.Id) }));
            return;
        }
        if (lists.Count == 0)
        {
            Out.Say("The registry has no package lists yet.");
            return;
        }
        Out.Table(lists.Select(l => new[] { new Span(l.Id, Tone.Accent), new Span(l.Name), new Span(Plural(l.Packages.Count, "package"), Tone.Dim), new Span(Out.Piped ? l.Description : Out.Truncate(l.Description, 60), Tone.Dim) }).ToList(), indent: 0);
        if (!Out.Piped) Out.Hint($"Add one with '{Prefix}install-package-list <id>'.");
    }

    private async Task InstallPackageListAsync(List<string> values)
    {
        var options = values.ToList();
        var assumeYes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        Expect(options, 1, 1, "a package list id or .json file");
        var value = options[0];
        PackageList list;
        if (File.Exists(FullPath(value)) || value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(FullPath(value))) throw new FileNotFoundException($"There's no file {FullPath(value)}.");
            try { list = JsonSerializer.Deserialize<PackageList>(await File.ReadAllTextAsync(FullPath(value)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PackageList(); }
            catch (JsonException ex) { throw new InvalidOperationException($"{value} isn't a package list: {ex.Message}"); }
            if (list.Packages.Count == 0) throw new InvalidOperationException($"{value} has no packages in it.");
        }
        else
        {
            var settings = await _settings.LoadAsync();
            List<PackageList> lists = null!;
            await RunOperationAsync(async ct => lists = await _packageLists.TryLoadAsync(settings.CatalogUrl, ct) ?? throw new InvalidOperationException("Couldn't reach the package registry to load its package lists. Check your connection, then try again."), "Loading package lists" + Out.Ellipsis);
            list = lists.FirstOrDefault(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException(
                $"There's no package list '{value}'." + (Suggest(value, lists.Select(l => l.Id)) is { Count: > 0 } s ? $" Did you mean {JoinOr(s.Select(x => $"'{x}'"))}?" : $" '{Prefix}list-package-lists' shows them."));
        }
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var shipped = await LoadShippedPackagesAsync(install);
        await LoadAdvisoriesAsync();
        await ReloadManifestAsync(install);
        int added = 0, present = 0, refused = 0;
        var unlisted = new List<PackageListEntry>();
        foreach (var package in list.Packages)
        {
            if (string.IsNullOrWhiteSpace(package.Id)) continue;
            if (IsPresentInSource(install, shipped, package.Id) || shipped.IsIncluded(package.Id) || HasDependency(install, package.Id)) { present++; continue; }
            var listed = _catalogs.AllLatest(catalog).FirstOrDefault(p => p.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase));
            if (listed is null && !string.IsNullOrWhiteSpace(package.GitUrl))
            {
                if (!GitUrlPolicy.IsSafeDependencyUrl(package.GitUrl) || !BasisDevStore.IsValidId(package.Id))
                {
                    Out.Warning($"Skipped {package.Id}: unsupported git URL.");
                    refused++;
                }
                else unlisted.Add(package);
                continue;
            }
            AddCatalogDependencies(install, catalog, new[] { (package.Id, string.IsNullOrWhiteSpace(package.Version) ? "*" : package.Version!) }, shipped);
            if (!HasDependency(install, package.Id) && !string.IsNullOrWhiteSpace(package.Version)) install.Manifest.Dependencies[package.Id] = package.Version.Trim();
            if (HasDependency(install, package.Id)) added++;
        }
        await UnityProjectService.SaveManifestAsync(install.UnityProjectPath, install.Manifest);
        foreach (var package in unlisted)
        {
            var label = string.IsNullOrWhiteSpace(package.Name) ? package.Id : package.Name;
            try
            {
                await RefuseAdvisedAsync(package.Id, package.GitUrl, label);
                await CloneInstallAsync(install, package.Id, package.GitUrl!.Trim(), false, ReviewGate($"{label} from {list.Name} isn't in the Basis registry, so nobody has checked it for you.", null, false, assumeYes), reviewRequired: true);
                added++;
            }
            catch (DeclinedException)
            {
                Out.Warning($"Left {label} uninstalled.");
                refused++;
            }
            catch (InvalidOperationException ex)
            {
                Out.Error($"{label}: {ex.Message}");
                refused++;
            }
        }
        Out.Success($"Added {Plural(added, "package")} from {list.Name} to {install.DisplayName}" + (present > 0 ? $"; {present} {(present == 1 ? "was" : "were")} already there" : "") + (refused > 0 ? $"; {refused} skipped" : "") + ".");
        if (added > 0) Out.Hint("Unity downloads them the next time it opens or refreshes the project.");
    }

    private async Task ExportPackageListAsync(List<string> values)
    {
        var options = values.ToList();
        var name = TakeValue(options, "--name");
        var description = TakeValue(options, "--description");
        Expect(options, 0, 1, "a file to write");
        var install = await LoadInstallAsync();
        var catalog = await LoadCatalogAsync();
        var shipped = await LoadShippedPackagesAsync(install);
        var sources = _catalogs.AllLatest(catalog).ToDictionary(v => v.Name, v => v.Source, StringComparer.OrdinalIgnoreCase);
        var entries = new List<PackageListEntry>();
        foreach (var (id, value) in install.Manifest.Dependencies)
        {
            var isVersion = !string.IsNullOrWhiteSpace(value) && SemVerRange.TryParse(value, out _);
            if (string.IsNullOrWhiteSpace(value) || (isVersion && id.StartsWith("com.unity", StringComparison.OrdinalIgnoreCase)) || shipped.IsBuiltIn(id, sources.GetValueOrDefault(id)) || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add(new PackageListEntry { Id = id, GitUrl = isVersion ? null : value, Version = isVersion ? value : null });
        }
        foreach (var mount in _mounts.ListMounts(install.UnityProjectPath).Where(m => MountService.IsWorkingClone(m.FolderPath)))
            if (!entries.Any(e => e.Id.Equals(mount.PackageId, StringComparison.OrdinalIgnoreCase)) && !shipped.IsBuiltIn(mount.PackageId, sources.GetValueOrDefault(mount.PackageId)) && UpmGitUrl.Parse(mount.OriginalManifestValue) is not null)
                entries.Add(new PackageListEntry { Id = mount.PackageId, GitUrl = mount.OriginalManifestValue });
        if (entries.Count == 0) throw new InvalidOperationException($"{install.DisplayName} has no packages on top of Basis to put in a list.");
        GitStatus? git = install.IsGitRepo ? await _git.GetStatusAsync(install.RepoRoot) : null;
        var listName = string.IsNullOrWhiteSpace(name) ? install.DisplayName : name.Trim();
        var list = new PackageList
        {
            Id = Slugify(listName), Name = listName, Description = description ?? "", Author = Environment.UserName,
            BasisBranch = git?.Branch, BasisCommit = git?.ShortCommit, Unity = install.UnityVersion, Icon = "🧩",
            Packages = entries.OrderByDescending(e => e.GitUrl is not null).ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        if (options.Count == 1 && options[0] == "-")
        {
            Console.Out.Write(json + "\n");
            return;
        }
        var file = FullPath(options.Count == 1 ? options[0] : list.Id + ".json");
        if (File.Exists(file)) throw new InvalidOperationException($"{file} already exists. Pick another file name.");
        await File.WriteAllTextAsync(file, json + "\n");
        Out.Success($"Saved {Plural(list.Packages.Count, "package")} to {file}.");
        Out.Hint($"Others can add the same packages with '{Prefix}install-package-list \"{file}\"'.");
    }

    private static string Slugify(string text)
    {
        var slug = new string(text.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return slug.Length == 0 ? "packagelist" : slug;
    }

    private async Task ReloadManifestAsync(BasisInstall install) =>
        install.Manifest = (await _projects.LoadAsync(install.UnityProjectPath)).Manifest;

    private bool IsPresentInSource(BasisInstall install, ShippedPackages shipped, string id) =>
        shipped.Mounted.Contains(id) || shipped.Local(id) is not null
        || _mounts.FindMount(install.UnityProjectPath, id) is { } mount && Directory.Exists(mount.FolderPath);

    private MountRecord? WorkingCloneMount(BasisInstall install, string id) =>
        _mounts.FindMount(install.UnityProjectPath, id) is { } mount && MountService.IsWorkingClone(mount.FolderPath) ? mount : null;

    private IReadOnlyDictionary<string, MountRecord> WorkingCloneMounts(BasisInstall install) =>
        _mounts.ListMounts(install.UnityProjectPath).Where(m => MountService.IsWorkingClone(m.FolderPath))
            .GroupBy(m => m.PackageId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private string? MountedPackageRoot(BasisInstall install, string id)
    {
        if (WorkingCloneMount(install, id) is not { } mount) return null;
        var root = !File.Exists(Path.Combine(mount.FolderPath, "package.json")) && UpmGitUrl.Parse(mount.OriginalManifestValue)?.Path is { Length: > 0 } sub
            ? Path.Combine(mount.FolderPath, sub)
            : mount.FolderPath;
        return File.Exists(Path.Combine(root, "package.json")) ? root : null;
    }

    private async Task<string?> SyncServerSideAsync(BasisInstall install, CatalogPackageVersion entry, string url)
    {
        if (!entry.Server || !ServerPackageService.SupportsPackages(install.RepoRoot) || url.Length == 0) return null;
        try
        {
            var mounted = MountedPackageRoot(install, entry.Name);
            ServerPackageResult result = null!;
            if (_serverPackages.LoadManifest(install.RepoRoot).Dependencies.ContainsKey(entry.Name))
            {
                var linked = _serverPackages.LoadLinks(install.RepoRoot).ContainsKey(entry.Name);
                await RunOperationAsync(async ct => result = await _serverPackages.UpdateAsync(install.RepoRoot, entry.Name, linked ? null : UpmGitUrl.Parse(url)?.Ref, false, Out.Progress, ct), "Updating the Basis Server side" + Out.Ellipsis);
            }
            else if (mounted is not null) result = await _serverPackages.InstallLinkedAsync(install.RepoRoot, url, mounted, entry.Name);
            else await RunOperationAsync(async ct => result = await _serverPackages.InstallAsync(install.RepoRoot, url, entry.Name, Out.Progress, ct), "Adding the Basis Server side" + Out.Ellipsis);
            return result.Ok ? null : result.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Installing the Basis Server side of {entry.Name}", ex);
            return ex.Message;
        }
    }

    private async Task<string?> RemoveServerSideAsync(BasisInstall install, string id, string? mountedRoot)
    {
        try
        {
            if (!ServerPackageService.HasServer(install.RepoRoot) || !_serverPackages.LoadManifest(install.RepoRoot).Dependencies.ContainsKey(id)) return null;
            var linkedToUnity = mountedRoot is not null && _serverPackages.LoadLinks(install.RepoRoot).TryGetValue(id, out var link) && Platform.PathsEqual(link, mountedRoot);
            var serverPackage = _catalogs.AllLatest(await LoadCatalogAsync()).Any(e => e.Server && e.Name.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (!linkedToUnity && !serverPackage) return null;
            var result = await _serverPackages.RemoveAsync(install.RepoRoot, id);
            return result.Ok ? null : result.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DiagnosticLog.Write($"Removing the Basis Server side of {id}", ex);
            return ex.Message;
        }
    }

    private async Task<string?> LatestReleaseUrlAsync(string gitUrl)
    {
        var location = UpmGitUrl.Parse(gitUrl);
        if (location is null || location.Ref is not null) return null;
        try
        {
            var versions = await _versions.GetVersionsAsync(gitUrl, await TryGitHubTokenAsync());
            return versions.LatestStable?.Ref is { } tag ? location.ToManifestUrl(tag, location.Path) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            DiagnosticLog.Write($"Resolving the latest release of {gitUrl}", ex);
            return null;
        }
    }

    private async Task<string?> TryGitHubTokenAsync()
    {
        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value && value.Trim().Length > 0) return value.Trim();
        try { return _ghAuth.GhAvailable ? await _ghAuth.GetTokenAsync() : null; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            DiagnosticLog.Write("Reading the GitHub CLI token", ex);
            return null;
        }
    }

    // Same rules as the app's Packages tab: built-in packages are hidden from the default list and
    // shown with --built-in; mounting one does not change where it came from.
    private async Task<ShippedPackages> LoadShippedPackagesAsync(BasisInstall install)
    {
        var mounted = _mounts.ListMounts(install.UnityProjectPath).Where(m => MountService.IsWorkingClone(m.FolderPath))
            .Select(m => m.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var basis = _git.IsAvailable ? await _updates.GetBasisPackagesAsync(install.UnityProjectPath) : new BasisPackages(new HashSet<string>(), null);
        var embedded = new Dictionary<string, LocalPackage>(StringComparer.OrdinalIgnoreCase);
        var own = new Dictionary<string, LocalPackage>(StringComparer.OrdinalIgnoreCase);
        var shadowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in _projects.ListEmbeddedPackages(install.UnityProjectPath))
        {
            var fromBasis = basis.EmbeddedFolders?.Contains(package.FolderName) ?? true;
            if (!mounted.Contains(package.Id)) (fromBasis ? embedded : own)[package.Id] = package;
            else if (!package.IsGitRepo && fromBasis) shadowed.Add(package.Id);
        }
        return new ShippedPackages(mounted, embedded, own, shadowed, basis.Dependencies, install.Manifest, basis.EmbeddedFolders is not null);
    }

    private sealed record ShippedPackages(IReadOnlySet<string> Mounted, IReadOnlyDictionary<string, LocalPackage> Embedded, IReadOnlyDictionary<string, LocalPackage> Own,
        IReadOnlySet<string> Shadowed, IReadOnlySet<string> BasisDependencies, PackageManifest Manifest, bool Known)
    {
        public bool IsBuiltIn(string id, string? source) =>
            Embedded.ContainsKey(id) || Shadowed.Contains(id) || BasisDependencies.Contains(id)
            || source?.Trim().ToLowerInvariant() is "built-in" or "builtin";

        public bool IsIncluded(string id) =>
            Embedded.ContainsKey(id) || Shadowed.Contains(id)
            || (BasisDependencies.Contains(id) && (Mounted.Contains(id) || Manifest.Dependencies.Keys.Any(k => k.Equals(id, StringComparison.OrdinalIgnoreCase))));

        public LocalPackage? Local(string id) => Embedded.GetValueOrDefault(id) ?? Own.GetValueOrDefault(id);

        // Packages the catalog doesn't list but the project mounts, embeds or pulls from git / file:.
        public IEnumerable<string> LocalIds(Catalog catalog) =>
            Mounted.Concat(Embedded.Keys).Concat(Own.Keys)
                .Concat(Manifest.Dependencies.Where(d => LooksMountable(d.Value)).Select(d => d.Key))
                .Where(id => !catalog.Packages.ContainsKey(id))
                .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool LooksMountable(string? manifestValue) =>
        !string.IsNullOrWhiteSpace(manifestValue) &&
        (manifestValue.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || UpmGitUrl.Parse(manifestValue) is not null);
}
