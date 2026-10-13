using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private const int ReviewPlaces = 3;

    private sealed class DeclinedException() : InvalidOperationException("The package wasn't installed.");

    private static readonly Dictionary<PackageCapability, string> CapabilityTitles = new()
    {
        [PackageCapability.RunsInCompiler] = "Runs code inside Unity's compiler",
        [PackageCapability.StartsPrograms] = "Starts other programs or commands",
        [PackageCapability.LoadsHiddenCode] = "Loads code that isn't in its scripts",
        [PackageCapability.ReadsLogins] = "Looks for passwords, tokens or wallets",
        [PackageCapability.SystemSettings] = "Reads or changes the Windows registry",
        [PackageCapability.Programs] = "Contains programs or scripts",
        [PackageCapability.NativeCode] = "Uses native code",
        [PackageCapability.CompiledCode] = "Contains compiled code",
        [PackageCapability.RunsAutomatically] = "Runs code by itself when Unity loads, imports, plays or builds",
        [PackageCapability.Network] = "Uses the internet",
        [PackageCapability.AddsPackages] = "Adds other packages",
        [PackageCapability.EncodedData] = "Contains large encoded data",
    };

    private readonly HashSet<string> _unofficialIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<PackageAdvisory>? _advisoryList;

    private async Task<IReadOnlyList<PackageAdvisory>> LoadAdvisoriesAsync()
    {
        if (_advisoryList is not null) return _advisoryList;
        if (_advisoryService is null) return _advisoryList = Array.Empty<PackageAdvisory>();
        var settings = await _settings.LoadAsync();
        return _advisoryList = await _advisoryService.LoadAsync(settings.CatalogUrl);
    }

    private async Task RefuseAdvisedAsync(string id, string? url, string displayName)
    {
        if (AdvisoryService.Match(await LoadAdvisoriesAsync(), id, url, null, unknownCommitMatches: false) is { Blocks: true } advisory)
            throw new InvalidOperationException($"{displayName} was pulled from the Basis registry and can't be installed: {advisory.Title}");
    }

    private Func<PackageReview, Task<bool>> ReviewGate(string reason, CatalogPackageVersion? entry, bool registry, bool assumeYes) => async review =>
    {
        var advisory = AdvisoryService.Match(await LoadAdvisoriesAsync(), review.PackageId, review.Source.CloneUrl, review.Commit);
        var reviewed = registry ? PackageTrust.ReviewedCommits(_catalog, entry?.Name, entry) : Array.Empty<string>();
        var trusted = PackageTrust.IsTrusted(registry, reviewed, review.Commit);
        var pinned = reviewed.FirstOrDefault(c => string.Equals(c, entry?.ReviewedCommit?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? reviewed.FirstOrDefault();
        if (trusted && advisory is null) return true;
        if (registry && !trusted) reason = $"The Basis registry checked {PackageScanService.ShortCommit(pinned)} of {review.DisplayName}; this is {PackageScanService.ShortCommit(review.Commit)}, which it hasn't checked.";
        var scan = await PackageScanService.ScanAsync(review.PackageRoot);
        Out.StopStatus();
        if (advisory is not null) Out.Error($"{review.DisplayName} {(advisory.Blocks ? "was pulled from" : "has a warning in")} the Basis registry: {advisory.Title}{(string.IsNullOrWhiteSpace(advisory.Details) ? "" : " " + advisory.Details.Trim())}");
        if (advisory?.Blocks == true) return false;
        if (!trusted) Out.Warning(reason);
        PrintScan(review.DisplayName, review.Source, review.Package?.Version, review.Commit, scan);
        Out.Hint("Unity runs a package's editor scripts as soon as it loads it, with your full access. These checks look for common warning signs; they can't prove a package is safe.");
        if (assumeYes)
        {
            Out.Warning("Installing it because --yes was given.");
            return true;
        }
        return Confirm($"Install {review.DisplayName}?");
    };

    private static void PrintScan(string name, UpmGitUrl? source, string? version, string? commit, PackageScan scan)
    {
        var rows = new List<Span[]>();
        if (source is not null) rows.Add(new Span[] { "Source", $"{source.Host}/{source.Slug}{(string.IsNullOrEmpty(source.Path) ? "" : "  ·  " + source.Path)}" });
        var at = string.Join("  ·  ", new[] { version, source?.Ref, string.IsNullOrEmpty(commit) ? null : PackageScanService.ShortCommit(commit) }.Where(p => !string.IsNullOrEmpty(p)).Distinct());
        if (at.Length > 0) rows.Add(new Span[] { "Version", at });
        rows.Add(new Span[] { "Checked", $"{Plural(scan.Files, "file")}, {Plural(scan.Scripts, "script")}, {Plural(scan.Binaries, "compiled file")}{(scan.Incomplete ? " (too big to check everything)" : "")}" });
        Out.Say(new Span(name, Tone.Accent));
        Out.Table(rows);
        if (scan.Findings.Count == 0)
        {
            Out.Say("Nothing stood out in its files. That doesn't prove it's safe.");
            return;
        }
        foreach (var capability in scan.Capabilities)
        {
            var high = scan.RiskOf(capability) == PackageRisk.High;
            Out.Say(new Span(high ? "x " : "! ", high ? Tone.Bad : Tone.Warn), new Span(CapabilityTitles[capability], Tone.Bold));
            var found = scan.Findings.Where(f => f.Capability == capability).ToList();
            foreach (var finding in found.Take(ReviewPlaces)) Out.Say(new Span($"    {finding.Location}  ·  {finding.Evidence}", Tone.Dim));
            if (found.Count > ReviewPlaces) Out.Say(new Span($"    and {found.Count - ReviewPlaces} more", Tone.Dim));
        }
    }

    private async Task<IReadOnlyList<(string Id, PackageAdvisory Advisory)>> PulledPackagesAsync(BasisInstall install)
    {
        var advisories = await LoadAdvisoriesAsync();
        if (advisories.Count == 0 || !install.HasUnityProject) return Array.Empty<(string, PackageAdvisory)>();
        var embedded = _projects.ListEmbeddedPackages(install.UnityProjectPath);
        var found = new List<(string, PackageAdvisory)>();
        foreach (var id in advisories.Select(a => a.Package.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = install.Manifest.Dependencies.Keys.FirstOrDefault(k => k.Equals(id, StringComparison.OrdinalIgnoreCase));
            var mount = _mounts.FindMount(install.UnityProjectPath, id);
            var local = embedded.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (key is null && mount is null && local is null) continue;
            var (repository, commit) = AdvisoryService.InstalledSource(install.UnityProjectPath, id, mount?.OriginalManifestValue ?? (key is null ? null : install.Manifest.Dependencies[key]), mount?.FolderPath);
            if (AdvisoryService.Match(advisories, id, repository, commit) is { } advisory) found.Add((key ?? id, advisory));
        }
        return found;
    }

    private async Task ScanPackageAsync(List<string> values)
    {
        var options = values.ToList();
        var strict = TakeFlag(options, "--strict");
        Expect(options, 1, 1, "a package folder or git URL");
        var target = options[0];
        PackageScan scan;
        UpmGitUrl? source = null;
        string? version = null, commit = null, temp = null;
        try
        {
            string root;
            if (Directory.Exists(FullPath(target))) root = FullPath(target);
            else
            {
                source = UpmGitUrl.Parse(target) ?? throw new InvalidOperationException($"'{target}' is neither a folder nor a git URL.");
                if (!GitUrlPolicy.IsSafeDependencyUrl(target)) throw new InvalidOperationException($"Refusing the unsupported git URL {target}.");
                if (!_git.IsAvailable) throw new InvalidOperationException("Git was not found on PATH, so the package can't be downloaded.");
                temp = Path.Combine(Path.GetTempPath(), "basispm-scan-" + Guid.NewGuid().ToString("N"));
                GitResult clone = null!;
                await RunOperationAsync(async ct => clone = await _git.CloneAtAsync(source.CloneUrl, temp, source.Ref, Out.Progress, ct), $"Downloading {source.Slug}" + Out.Ellipsis);
                if (!clone.Ok) throw new InvalidOperationException($"Couldn't download {source.Slug}: {clone.Output}");
                commit = await _git.ResolveCommitAsync(temp, "HEAD");
                root = BasisDevStore.PackageRoot(temp, source.Path) ?? (source.Path is null ? temp : Path.Combine(temp, source.Path));
            }
            version = BasisPackagesStore.ReadVersion(root);
            scan = null!;
            await RunOperationAsync(async ct => scan = await PackageScanService.ScanAsync(root, ct), "Checking its files" + Out.Ellipsis);
        }
        finally
        {
            if (temp is not null)
            {
                try { await BasisInstallService.DeleteFolderAsync(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting the scan download {temp}", ex); }
            }
        }
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                target, commit, version, files = scan.Files, scripts = scan.Scripts, binaries = scan.Binaries, incomplete = scan.Incomplete, highRisk = scan.HasHighRisk,
                findings = scan.Findings.Select(f => new { capability = f.Capability.ToString(), risk = f.Risk.ToString(), file = f.File, line = f.Line, evidence = f.Evidence }),
            });
        }
        else PrintScan(source?.Slug ?? Path.GetFileName(FullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), source, version, commit, scan);
        if (strict && scan.HasHighRisk) _exitCode = 1;
    }
}
