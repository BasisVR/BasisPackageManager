using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private async Task BasisDevAsync(List<string> values)
    {
        var options = values.ToList();
        var all = TakeFlag(options, "--all");
        var force = TakeFlag(options, "--force");
        var install = await LoadInstallAsync();
        if (options.Count == 0)
        {
            BasisDevReport report = null!;
            await RunOperationAsync(async ct => report = await _basisDev.ScanAsync(install, compare: true, ct), "Comparing development clones" + Out.Ellipsis);
            PrintBasisDev(install, report, all);
            return;
        }

        var verb = options[0].ToLowerInvariant();
        BasisDevResult result = null!;
        if (verb == "reconcile")
        {
            Expect(options, 1, 1, "");
            await RunOperationAsync(async ct => result = await _basisDev.ReconcileAsync(install, ct), "Reconciling" + Out.Ellipsis);
        }
        else
        {
            var action = verb switch
            {
                "record" => BasisDevAction.Record,
                "forget" => BasisDevAction.Prune,
                "use" => BasisDevAction.UseClone,
                "release" => BasisDevAction.Release,
                "restore" => BasisDevAction.Restore,
                "reclone" => BasisDevAction.Reclone,
                "ignore" => BasisDevAction.Ignore,
                "unignore" => BasisDevAction.Unignore,
                _ => throw new UsageException($"'basisdev {options[0]}' isn't something basisdev can do." + (Suggest(options[0], BasisDevVerbs) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : "")),
            };
            Expect(options, 2, 2, "a package id");
            await RunOperationAsync(async ct => result = await _basisDev.ApplyAsync(install, options[1], action, force, Out.Progress, ct), $"Working on {options[1]}" + Out.Ellipsis);
        }
        if (!result.Ok) throw new InvalidOperationException(result.NeedsForce ? result.Message + " Add --force to do that." : result.Message);
        Out.Success(result.Message);
    }

    private static readonly string[] BasisDevVerbs = { "reconcile", "record", "forget", "use", "release", "restore", "reclone", "ignore", "unignore" };

    private void PrintBasisDev(BasisInstall install, BasisDevReport report, bool all)
    {
        var counts = report.Packages.Where(p => p.Source is not (PackageSourceKind.None or PackageSourceKind.Registry))
            .GroupBy(p => p.Source).OrderBy(g => g.Key).Select(g => $"{g.Count()} {DescribeSource(g.Key, install)}");
        Out.Say(new Span("Package sources: ", Tone.Dim), new Span(string.Join(", ", counts)));
        var stale = report.Packages.Where(p => p.Issues.Count == 1 && p.Issues[0] == BasisDevIssueKind.StaleRecord).ToList();
        var shown = report.Packages.Where(p => !stale.Contains(p) && (all ? p.Source != PackageSourceKind.None || p.Issues.Count > 0 : p.CloneFolder is not null || p.Issues.Count > 0)).ToList();
        if (shown.Count == 0 && stale.Count == 0)
        {
            Out.Success($"No {BasisDevStore.FolderName} clones or mount records to reconcile.");
            return;
        }
        foreach (var package in shown)
        {
            Out.Say();
            Out.Say(new Span(package.Id, package.Issues.Count > 0 ? Tone.Warn : Tone.Bold), new Span("  " + DescribeSource(package.Source, install), Tone.Dim));
            if (package.EmbeddedFolder is not null) Detail("folder", $"{Relative(install, package.EmbeddedFolder)}{(package.EmbeddedTracked ? $" (tracked by the {DescribeSource(PackageSourceKind.ProjectRepo, install)})" : "")}");
            if (package.CloneFolder is not null)
                Detail("clone", $"{Relative(install, package.CloneFolder)} ({(package.HandledByProject ? $"not used, handled by the {DescribeSource(PackageSourceKind.ProjectRepo, install)}" : package.CloneExists ? package.CloneActive ? "in use" : "not in use" : Directory.Exists(package.CloneFolder) ? "no longer a clone" : "missing")})");
            if (package.Upstream is { } upstream) Detail("upstream", $"{DescribeUpstream(upstream)}{(package.Sidecar is null ? " (not recorded yet)" : "")}");
            if (package.Comparison is { } comparison) Detail("compare", DescribeComparison(comparison));
            foreach (var issue in package.Issues.Order()) Out.Say(new Span("    ! ", Tone.Warn), new Span(DescribeIssue(issue, package)));
            if (package.Issues.Count > 0 && package.Actions.Count > 0)
                Out.Say(new Span("    fix       ", Tone.Dim), new Span(string.Join("  |  ", package.Actions.Select(a => $"{Prefix}basisdev {ActionVerb(a)} {package.Id}")), Tone.Accent));
        }
        if (stale.Count > 0)
        {
            Out.Say();
            Out.Warning($"{Plural(stale.Count, "mount record")} point at folders that are no longer clones (the files are untouched):");
            foreach (var package in stale) Out.Say($"    {package.Id,-44} {Relative(install, package.CloneFolder!)} ({DescribeSource(package.Source, install)})");
        }
        var record = report.Packages.Count(p => p.Actions.Contains(BasisDevAction.Record));
        var forget = report.Packages.Count(p => p.Actions.Contains(BasisDevAction.Prune));
        if (record + forget > 0)
        {
            Out.Say();
            Out.Hint($"'{Prefix}basisdev reconcile' records {Plural(record, "clone")} and forgets {Plural(forget, "stale mount record")}. It doesn't touch package files or manifest.json.");
        }
    }

    private static void Detail(string label, string text) => Out.Say(new Span("    " + label.PadRight(10), Tone.Dim), new Span(text));

    private static string DescribeSource(PackageSourceKind source, BasisInstall install) => source switch
    {
        PackageSourceKind.ProjectRepo => install.IsBasisCheckout ? "Basis repo" : "project repo",
        PackageSourceKind.LocalFolder => "local folder",
        PackageSourceKind.DevClone => "dev clone",
        PackageSourceKind.LocalPath => "local path",
        PackageSourceKind.Tarball => "tarball",
        PackageSourceKind.Git => "git",
        PackageSourceKind.Registry => "registry",
        _ => "not installed",
    };

    private static string DescribeIssue(BasisDevIssueKind issue, BasisDevPackage package) => issue switch
    {
        BasisDevIssueKind.MissingClone => "The clone is gone, so Unity can't load this package. Restore the original manifest line or clone it again.",
        BasisDevIssueKind.Detached => package.ManifestValue is { } line
            ? $"Not in use: manifest.json points at {line} instead."
            : "Not in use: manifest.json has no entry for this package.",
        BasisDevIssueKind.Shadowed => $"Not in use: Unity loads Packages/{Path.GetFileName(package.EmbeddedFolder)} instead.",
        BasisDevIssueKind.StaleRecord => "The mount record points at a folder that is no longer a clone.",
        _ => $"No {BasisDevStore.FolderName} sidecar yet, so its upstream isn't recorded.",
    };

    private static string ActionVerb(BasisDevAction action) => action switch
    {
        BasisDevAction.Record => "record",
        BasisDevAction.Prune => "forget",
        BasisDevAction.UseClone => "use",
        BasisDevAction.Release => "release",
        BasisDevAction.Restore => "restore",
        BasisDevAction.Ignore => "ignore",
        BasisDevAction.Unignore => "unignore",
        _ => "reclone",
    };

    private static string DescribeUpstream(BasisDevUpstream upstream)
    {
        var text = string.IsNullOrWhiteSpace(upstream.Url) ? "(no origin)" : upstream.Url;
        if (!string.IsNullOrWhiteSpace(upstream.Path)) text += $" ?path={upstream.Path}";
        if (!string.IsNullOrWhiteSpace(upstream.Ref)) text += $" #{upstream.Ref}";
        if (!string.IsNullOrWhiteSpace(upstream.Commit)) text += $" @ {Short(upstream.Commit)}";
        return text;
    }

    private static string DescribeComparison(BasisDevComparison comparison)
    {
        if (comparison.Identical) return $"identical to the clone (upstream {Short(comparison.MatchingCommit)})";
        var text = $"{comparison.Changed} file(s) differ from the clone ({comparison.Added} only here, {comparison.Removed} only upstream, {comparison.Modified} changed)";
        return text + (comparison.MatchingCommit is { } match ? $"; same as upstream {Short(match)}" : "; matches no upstream commit");
    }

    private static string Relative(BasisInstall install, string folder) =>
        Path.GetRelativePath(install.UnityProjectPath, folder).Replace('\\', '/');
}
