using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record PackageCheckSummary(PackageVerdict Verdict, bool FromProject, bool Stale, PackageCheckState State);

public static class PackageCheckVerdicts
{
    public static PackageCheckState StateFor(ProjectCheckReport report, string packageId, string? version = null, string? commit = null)
    {
        var issues = report.IssuesFor(packageId).ToList();
        var errors = issues.Count(i => i.Severity == IssueSeverity.Error);
        var warnings = issues.Count(i => i.Severity == IssueSeverity.Warning);
        var compile = report.Compile;
        var compiled = compile is { Status: CompileCheckStatus.Passed or CompileCheckStatus.Failed };
        var built = compile is { Status: CompileCheckStatus.Passed }
            || compile is { Status: CompileCheckStatus.Failed } && report.Uncompiled is { } uncompiled && !uncompiled.Contains(packageId, StringComparer.OrdinalIgnoreCase);
        var focused = report.Focus.Contains(packageId, StringComparer.OrdinalIgnoreCase);
        var caused = report.Attributed?.GetValueOrDefault(packageId)?.ToHashSet(StringComparer.Ordinal);
        var elsewhere = focused && errors == 0
            ? report.Issues.Where(i => i.Severity == IssueSeverity.Error && report.IsIntroduced(i) && (report.Attributed is null || caused?.Contains(PackageCheckKeys.Of(i)) == true)).ToList()
            : new List<PackageIssue>();
        var verdict = errors > 0 || elsewhere.Count > 0 ? PackageVerdict.Breaks
            : built ? warnings > 0 ? PackageVerdict.Warnings : PackageVerdict.Works
            : warnings > 0 ? PackageVerdict.Warnings
            : PackageVerdict.Unknown;
        var first = issues.Where(i => i.Severity == IssueSeverity.Error).Concat(elsewhere).Concat(issues.Where(i => i.Severity == IssueSeverity.Warning)).FirstOrDefault();
        return new PackageCheckState
        {
            Verdict = verdict,
            Version = version,
            Commit = commit,
            Errors = errors + elsewhere.Count,
            Warnings = warnings,
            Summary = first is null ? null : first.Location.Length == 0 ? first.Message : $"{first.Location}: {first.Message}",
            Compiled = compiled && (built || errors > 0 || elsewhere.Count > 0),
            Checked = report.Finished,
            BasisBranch = report.Basis?.Branch,
            BasisCommit = report.Basis?.Commit,
            Unity = report.UnityVersion,
            Tool = report.Tool,
        };
    }

    public static List<string>? Uncompiled(ProjectInventory inventory, CompileCheckResult? compile)
    {
        if (compile is not { Status: CompileCheckStatus.Failed }) return null;
        var errors = compile.Diagnostics.Where(d => d.Severity == IssueSeverity.Error).ToList();
        if (errors.Any(d => string.IsNullOrEmpty(d.Assembly))) return null;
        var blocked = compile.FailedAssemblies.Concat(errors.Select(d => d.Assembly!)).ToHashSet(StringComparer.Ordinal);
        if (blocked.Count == 0) return null;
        var asmdefs = inventory.Packages.SelectMany(p => p.Assemblies).Where(a => a.Error is null && a.Name.Length > 0).ToList();
        var byGuid = new Dictionary<string, AsmdefInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdef in asmdefs)
            if (asmdef.Guid is { } guid) byGuid.TryAdd(guid, asmdef);
        string? Resolve(string? reference) => string.IsNullOrEmpty(reference) ? null
            : reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) ? byGuid.GetValueOrDefault(reference[5..].Trim())?.Name
            : reference;
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var asmdef in asmdefs)
                if (!blocked.Contains(asmdef.Name) && asmdef.References.Any(r => Resolve(r) is { } name && blocked.Contains(name)))
                    grew |= blocked.Add(asmdef.Name);
        }
        if (asmdefs.Any(a => a.AutoReferenced && !a.IsEditorOnly && blocked.Contains(a.Name))) blocked.Add("Assembly-CSharp");
        if (blocked.Contains("Assembly-CSharp") || asmdefs.Any(a => a.AutoReferenced && blocked.Contains(a.Name))) blocked.Add("Assembly-CSharp-Editor");
        return inventory.Packages
            .Where(p => p.Assemblies.Any(a => blocked.Contains(a.Name)) || p.Sources.Any(s => Resolve(s.Assembly ?? s.AsmrefReference) is { } name && blocked.Contains(name)))
            .Select(p => p.Id).ToList();
    }

    public static Dictionary<string, List<string>>? Attribute(ProjectCheckReport report, ProjectInventory inventory)
    {
        if (report.Focus.Count == 0) return null;
        var introduced = report.Issues.Where(i => i.Severity == IssueSeverity.Error && report.IsIntroduced(i)).ToList();
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in report.Focus)
        {
            var sees = AssembliesThatSee(inventory, id);
            var words = DllWords(inventory.Find(id));
            map[id] = introduced.Where(i => !string.Equals(i.Package, id, StringComparison.OrdinalIgnoreCase) && MayBeCausedBy(i, id, sees, words)).Select(PackageCheckKeys.Of).ToList();
        }
        return map;
    }

    public static HashSet<string>? AssembliesThatSee(ProjectInventory inventory, string packageId)
    {
        if (inventory.Find(packageId) is not { } package) return null;
        var asmdefs = inventory.Packages.SelectMany(p => p.Assemblies).Where(a => a.Error is null && a.Name.Length > 0).ToList();
        var byGuid = new Dictionary<string, AsmdefInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdef in asmdefs)
            if (asmdef.Guid is { } guid) byGuid.TryAdd(guid, asmdef);
        string? Resolve(string? reference) => string.IsNullOrEmpty(reference) ? null
            : reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) ? byGuid.GetValueOrDefault(reference[5..].Trim())?.Name
            : reference;
        var seen = package.Assemblies.Where(a => a.Error is null && a.Name.Length > 0).Select(a => a.Name)
            .Concat(package.Sources.Select(s => Resolve(s.Assembly ?? s.AsmrefReference)).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var asmdef in asmdefs)
                if (!seen.Contains(asmdef.Name) && asmdef.References.Any(r => Resolve(r) is { } name && seen.Contains(name)))
                    grew |= seen.Add(asmdef.Name);
            if (!seen.Contains("Assembly-CSharp") && asmdefs.Any(a => a.AutoReferenced && !a.IsEditorOnly && seen.Contains(a.Name))) grew |= seen.Add("Assembly-CSharp");
            if (!seen.Contains("Assembly-CSharp-Editor") && (seen.Contains("Assembly-CSharp") || asmdefs.Any(a => a.AutoReferenced && seen.Contains(a.Name)))) grew |= seen.Add("Assembly-CSharp-Editor");
        }
        return seen;
    }

    private static bool MayBeCausedBy(PackageIssue issue, string packageId, IReadOnlySet<string>? sees, IReadOnlyList<string> dllWords) => issue.Code switch
    {
        IssueCodes.CompileError => sees is null || string.IsNullOrEmpty(issue.Other) || sees.Contains(issue.Other) || Mentions(issue.Message, dllWords),
        IssueCodes.PackageResolution => true,
        _ => string.Equals(issue.Other, packageId, StringComparison.OrdinalIgnoreCase),
    };

    private static IReadOnlyList<string> DllWords(InspectedPackage? package) => package is null ? Array.Empty<string>() : package.Plugins.Where(p => p.IsManaged)
        .SelectMany(p => p.PublicTypes.Select(t => t[(t.LastIndexOf('.') + 1)..].Split('`')[0]).Concat(p.Namespaces).Append(p.AssemblyName ?? "").Append(Path.GetFileNameWithoutExtension(p.FileName)))
        .Where(w => w.Length >= 3).Distinct(StringComparer.Ordinal).ToList();

    private static bool Mentions(string message, IReadOnlyList<string> words) =>
        words.Any(w => System.Text.RegularExpressions.Regex.IsMatch(message, $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(w)}(?![\w])"));

    public static PackageCheckSummary? Summarize(PackageCheckState? local, PackageCheckState? registry, string? projectBasisCommit = null, string? packageCommit = null, string? registryHeadNow = null)
    {
        var localStale = local is not null && (Differs(local.BasisCommit, projectBasisCommit) || Differs(local.Commit, packageCommit));
        var registryStale = registry is not null && (Differs(registry.BasisCommit, registryHeadNow) || Differs(registry.Commit, packageCommit));
        if (local is not null && (!localStale || registry is null || registryStale)) return new PackageCheckSummary(local.Verdict, true, localStale, local);
        return registry is null ? null : new PackageCheckSummary(registry.Verdict, false, registryStale, registry);
    }

    public static bool SameCommit(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        var x = a.Trim();
        var y = b.Trim();
        var length = Math.Min(x.Length, y.Length);
        return length >= 7 && string.Compare(x, 0, y, 0, length, StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool Differs(string? recorded, string? current) =>
        !string.IsNullOrWhiteSpace(recorded) && !string.IsNullOrWhiteSpace(current) && !SameCommit(recorded, current);
}
