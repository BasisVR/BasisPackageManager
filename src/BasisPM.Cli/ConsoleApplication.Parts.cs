using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.Cli;

internal sealed partial class ConsoleApplication
{
    private static readonly string[] PartsVerbs = { "add", "remove" };

    private static string PartIds => string.Join(", ", BasisPartsService.All.Select(p => p.Id));

    private static IReadOnlyList<BasisPart> ParseParts(IEnumerable<string> names) => names
        .SelectMany(name => name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Select(name => BasisPartsService.Find(name) ?? throw new UsageException($"'{name}' isn't a part of Basis that can be left out. Parts: {PartIds}."))
        .Distinct().ToList();

    private async Task PartsAsync(List<string> values)
    {
        var options = values.ToList();
        var yes = TakeFlag(options, "--yes") | TakeFlag(options, "-y");
        var keepIgnored = TakeFlag(options, "--keep-ignored");
        var verb = options.Count == 0 ? null : options[0].ToLowerInvariant();
        if (verb is not (null or "add" or "remove"))
            throw new UsageException($"'parts {options[0]}' isn't something parts can do." + (Suggest(options[0], PartsVerbs) is { Count: > 0 } s ? $" Did you mean '{s[0]}'?" : ""));
        var named = verb is null ? Array.Empty<BasisPart>() : ParseParts(options.Skip(1));
        if (verb is not null && named.Count == 0) throw new UsageException($"Missing a part to {verb}. Parts: {PartIds}.");

        var install = await LoadInstallAsync();
        var parts = new BasisPartsService(_git);
        BasisPartsReport report = null!;
        await RunOperationAsync(async ct => report = await parts.ScanAsync(install.RepoRoot, install.UnityProjectPath, true, ct), "Checking the project's parts" + Out.Ellipsis);
        if (verb is null)
        {
            PrintParts(install, report);
            return;
        }
        if (report.Mode == BasisPartsMode.NotGitRepo) throw new InvalidOperationException($"{install.DisplayName} isn't a git repository, so parts can't be left out of it.");
        if (report.Mode == BasisPartsMode.Custom) throw new InvalidOperationException($"{install.DisplayName} uses its own sparse checkout ('git sparse-checkout list' shows it), so its folders are left as they are.");
        var missing = named.Where(p => report.Parts.All(s => s.Part != p)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException($"{install.DisplayName} has no {string.Join(" or ", missing.Select(p => p.Name))} folder to {verb}.");

        var leaveOut = report.LeftOut.ToHashSet();
        if (verb == "add") leaveOut.ExceptWith(named);
        else leaveOut.UnionWith(named);
        var removing = report.Parts.Where(p => p.Included && leaveOut.Contains(p.Part)).ToList();
        if (removing.SelectMany(p => p.Unsaved).ToList() is { Count: > 0 } unsaved)
            throw new InvalidOperationException(DescribePartsFailure(install, BasisPartsResult.Fail(BasisPartsFailure.UnsavedWork, string.Join('\n', unsaved))));
        var ignored = keepIgnored ? 0 : removing.Sum(p => p.IgnoredFiles);
        if (ignored > 0 && !yes && !Confirm($"Also delete the files git ignores in {string.Join(" and ", removing.Where(p => p.IgnoredFiles > 0).Select(p => p.Part.Name))} ({Plural(ignored, "file")}: build output and local settings, such as the server's config)?"))
        {
            Out.Note("Nothing changed. Pass --keep-ignored to leave those files where they are.");
            return;
        }

        BasisPartsResult result = null!;
        await RunOperationAsync(async ct => result = await parts.ApplyAsync(install.RepoRoot, install.UnityProjectPath, leaveOut.ToList(), !keepIgnored, ct),
            (verb == "add" ? "Adding " : "Leaving out ") + string.Join(" and ", named.Select(p => p.Name)) + Out.Ellipsis);
        if (!result.Ok) throw new InvalidOperationException(DescribePartsFailure(install, result));
        if (!result.Changed)
        {
            Out.Success($"Nothing to change: {string.Join(" and ", named.Select(p => p.Name))} {(named.Count == 1 ? "is" : "are")} already {(verb == "add" ? "included" : "left out")}.");
            return;
        }
        if (result.Removed.Count > 0) Out.Success($"Left {string.Join(" and ", result.Removed.Select(p => p.Name))} out of {install.DisplayName}. Git still has the files, so '{Prefix}parts add {string.Join(" ", result.Removed.Select(p => p.Id))}' brings them back.");
        if (result.Added.Count > 0) Out.Success($"Added {string.Join(" and ", result.Added.Select(p => p.Name))} back to {install.DisplayName}.");
        if (result.DeletedFiles > 0) Out.Note($"Deleted {Plural(result.DeletedFiles, "file")} that git ignores.");
        if (result.IgnoredFilesLeft > 0) Out.Note($"{Plural(result.IgnoredFilesLeft, "file")} that git ignores {(result.IgnoredFilesLeft == 1 ? "is" : "are")} still in the folder.");
        if (result.Kept.Count > 0) Out.Warning($"Kept {string.Join(", ", result.Kept.Take(5))}{(result.Kept.Count > 5 ? $" and {result.Kept.Count - 5} more" : "")}.");
    }

    private void PrintParts(BasisInstall install, BasisPartsReport report)
    {
        if (Out.JsonMode)
        {
            Out.Json(new
            {
                project = install.DisplayName,
                mode = report.Mode.ToString().ToLowerInvariant(),
                parts = report.Parts.Select(p => new { id = p.Part.Id, name = p.Part.Name, path = p.Path, included = p.Included, uncommitted = p.Unsaved, ignoredFiles = p.IgnoredFiles, repositories = p.Repositories }),
            });
            return;
        }
        switch (report.Mode)
        {
            case BasisPartsMode.NotGitRepo:
                Out.Warning($"{install.DisplayName} isn't a git repository, so parts can't be left out of it.");
                return;
            case BasisPartsMode.Custom:
                Out.Warning($"{install.DisplayName} uses its own sparse checkout, so its folders are left as they are.");
                break;
        }
        if (report.Parts.Count == 0)
        {
            Out.Note($"{install.DisplayName} has none of the parts that can be left out ({PartIds}).");
            return;
        }
        var rows = report.Parts.Select(p => new Span[]
        {
            new(p.Part.Id, Tone.Bold),
            new(p.Included ? "included" : "left out", p.Included ? Tone.Good : Tone.Warn),
            p.Path,
            new(DescribePartState(p), Tone.Dim),
        }).ToList();
        Out.Table(rows, new[] { "PART", "STATE", "FOLDER", "NOTES" });
        if (report.CanChange) Out.Hint($"'{Prefix}parts remove <part>' leaves a part out, '{Prefix}parts add <part>' brings it back. Git keeps the files either way.");
    }

    private static string DescribePartState(BasisPartState state)
    {
        var notes = new List<string>();
        if (state.Unsaved.Count > 0) notes.Add($"{Plural(state.Unsaved.Count, "uncommitted change")}");
        if (state.IgnoredFiles > 0) notes.Add($"{Plural(state.IgnoredFiles, "ignored file")}");
        if (state.Repositories.Count > 0) notes.Add($"{Plural(state.Repositories.Count, "git clone")} inside");
        return string.Join(", ", notes);
    }

    private static string DescribePartsFailure(BasisInstall install, BasisPartsResult result) => result.Failure switch
    {
        BasisPartsFailure.NotGitRepo => $"{install.DisplayName} isn't a git repository, so parts can't be left out of it.",
        BasisPartsFailure.CustomSparseCheckout => $"{install.DisplayName} uses its own sparse checkout, so its folders are left as they are.",
        BasisPartsFailure.OperationInProgress => $"Finish or undo the {result.Detail} in progress in {install.DisplayName} first.",
        BasisPartsFailure.UnsavedWork => $"These files have changes that aren't committed yet: {string.Join(", ", result.Detail.Split('\n').Take(5))}. Commit, move or discard them first.",
        _ => $"Git couldn't change which folders are checked out: {Tail(result.Detail)}",
    };
}
