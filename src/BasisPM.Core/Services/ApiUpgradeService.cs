using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed record ApiUpgradeEdit(string CloneFolder, string File, int Line, int Column, string From, string To)
{
    public string Package => Path.GetFileName(CloneFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}

public sealed class ApiUpgradeRecord
{
    public int Format { get; set; } = 1;
    public string? Unity { get; set; }
    public DateTimeOffset Applied { get; set; }
    public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    public List<ApiUpgradeChange> Changes { get; set; } = new();
}

public sealed class ApiUpgradeChange
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public static class ApiUpgradeService
{
    public const string RecordName = "basispm-apiupgrade.json";
    private const int FollowingLines = 3;

    private static readonly Encoding Bytes = Encoding.Latin1;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    private static readonly Regex Obsolete = new(@"^'(?<symbol>[^']+)'\s+is obsolete:\s+'(?<message>.*)'\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
    private static readonly Regex Upgradable = new(@"\(UnityUpgradable\)\s*->\s*(?:\[[^\]]*\]\s*)?(?<target>[A-Za-z_][\w.]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UseInstead = new(@"\b[Uu]se\s+(?:the\s+)?(?<target>[A-Za-z_][\w.]*)\s*(?:<[^>]*>)?\s*(?:\([^)]*\))?\s*(?:instead\b|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MoreSteps = new(@"^\s*,?\s*and\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (string From, string To)? RuleFor(CompileDiagnostic diagnostic)
    {
        if (diagnostic.Code is not ("CS0619" or "CS0618")) return null;
        var match = Obsolete.Match(diagnostic.Message.Trim());
        if (!match.Success) return null;
        var (container, from) = Split(match.Groups["symbol"].Value);
        var message = match.Groups["message"].Value;
        var target = Upgradable.Match(message) is { Success: true } upgradable ? upgradable.Groups["target"].Value
            : UseInstead.Match(message) is { Success: true } use && !MoreSteps.IsMatch(message[(use.Index + use.Length)..]) ? use.Groups["target"].Value
            : null;
        if (target is null) return null;
        var (targetContainer, to) = Split(target);
        if (!Identifier.IsMatch(from) || !Identifier.IsMatch(to) || from == to) return null;
        if (targetContainer.Length > 0 && targetContainer.Split('.')[^1] != container.Split('.')[^1]) return null;
        return (from, to);
    }

    public static IReadOnlyList<ApiUpgradeEdit> Plan(IEnumerable<CompileDiagnostic> diagnostics, string unityProjectPath)
    {
        var root = Path.GetFullPath(BasisDevStore.Root(unityProjectPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var edits = new List<ApiUpgradeEdit>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.File is null || diagnostic.Line is not > 0 || diagnostic.Column is not > 0 || RuleFor(diagnostic) is not { } rule) continue;
            var full = Path.GetFullPath(Path.IsPathRooted(diagnostic.File) ? diagnostic.File : Path.Combine(unityProjectPath, diagnostic.File));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, Platform.PathComparison())) continue;
            var inside = full[(root.Length + 1)..];
            var cut = inside.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (cut <= 0) continue;
            var clone = Path.Combine(root, inside[..cut]);
            if (!MountService.IsWorkingClone(clone) || !File.Exists(full)) continue;
            var edit = new ApiUpgradeEdit(clone, Path.GetRelativePath(clone, full).Replace('\\', '/'), diagnostic.Line.Value, diagnostic.Column.Value, rule.From, rule.To);
            if (!edits.Contains(edit)) edits.Add(edit);
        }
        return edits;
    }

    public static IReadOnlyList<ApiUpgradeEdit> Apply(IReadOnlyList<ApiUpgradeEdit> edits, string? unityVersion)
    {
        var applied = new List<ApiUpgradeEdit>();
        foreach (var clone in edits.GroupBy(e => e.CloneFolder, StringComparer.OrdinalIgnoreCase))
        {
            var record = Read(clone.Key) ?? new ApiUpgradeRecord();
            var changed = false;
            foreach (var file in clone.GroupBy(e => e.File, StringComparer.Ordinal))
            {
                var path = Path.Combine(clone.Key, file.Key);
                var lines = Lines(path);
                var done = new List<ApiUpgradeEdit>();
                foreach (var edit in file.OrderByDescending(e => e.Line).ThenByDescending(e => e.Column))
                    if (Rename(lines, edit.Line, edit.Column, edit.From, edit.To) is { } at) done.Add(edit with { Line = at.Line, Column = at.Column });
                if (done.Count == 0) continue;
                var bytes = Bytes.GetBytes(string.Concat(lines));
                File.WriteAllBytes(path, bytes);
                record.Files[file.Key] = Hash(bytes);
                record.Changes.AddRange(done.Select(d => new ApiUpgradeChange { File = file.Key, Line = d.Line, Column = d.Column, From = d.From, To = d.To }));
                applied.AddRange(done);
                changed = true;
            }
            if (!changed) continue;
            record.Unity = unityVersion ?? record.Unity;
            record.Applied = DateTimeOffset.UtcNow;
            Write(clone.Key, record);
        }
        return applied;
    }

    public static IReadOnlyList<ApiUpgradeEdit> KeepWorking(IReadOnlyList<ApiUpgradeEdit> applied, string unityProjectPath, IEnumerable<CompileDiagnostic> diagnosticsAfter)
    {
        var failing = diagnosticsAfter
            .Where(d => d.Severity == IssueSeverity.Error && d.File is not null && d.Line is > 0)
            .Select(d => (File: Path.GetFullPath(Path.IsPathRooted(d.File!) ? d.File! : Path.Combine(unityProjectPath, d.File!)), Line: d.Line!.Value))
            .ToList();
        var broken = applied
            .Where(e => failing.Any(f => Platform.PathsEqual(f.File, Path.GetFullPath(Path.Combine(e.CloneFolder, e.File))) && f.Line == e.Line))
            .Select(e => (e.CloneFolder, e.File))
            .Distinct()
            .ToList();
        foreach (var (clone, file) in broken) Revert(clone, file);
        return applied.Where(e => !broken.Contains((e.CloneFolder, e.File))).ToList();
    }

    public static ApiUpgradeRecord? Read(string clone)
    {
        var path = RecordPath(clone);
        if (path is null || !File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ApiUpgradeRecord>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading the API upgrade record of {clone}", ex);
            return null;
        }
    }

    public static ApiUpgradeRecord? Current(string clone)
    {
        if (Read(clone) is not { } record) return null;
        var kept = record.Files.Where(f => SameContent(Path.Combine(clone, f.Key), f.Value)).Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        if (kept.Count == 0) return null;
        record.Files = record.Files.Where(f => kept.Contains(f.Key)).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        record.Changes = record.Changes.Where(c => kept.Contains(c.File)).ToList();
        return record;
    }

    public static IReadOnlyList<GitFileChange> WithoutUpgrades(string clone, IReadOnlyList<GitFileChange> changes)
    {
        if (Read(clone) is not { Files.Count: > 0 } record) return changes;
        return changes.Where(c => !(c.Kind == GitChangeKind.Modified && record.Files.TryGetValue(c.Path, out var hash) && SameContent(Path.Combine(clone, c.Path), hash))).ToList();
    }

    public static int RevertAll(string clone)
    {
        if (Read(clone) is not { } record) return 0;
        var reverted = 0;
        foreach (var file in record.Files.Keys.ToList())
            if (Revert(clone, file)) reverted++;
        return reverted;
    }

    public static bool Revert(string clone, string file)
    {
        if (Read(clone) is not { } record || !record.Files.TryGetValue(file, out var hash)) return false;
        var path = Path.Combine(clone, file);
        var reverted = false;
        if (SameContent(path, hash))
        {
            var lines = Lines(path);
            foreach (var change in record.Changes.Where(c => c.File == file).OrderByDescending(c => c.Line).ThenByDescending(c => c.Column))
                Rename(lines, change.Line, change.Column, change.To, change.From, FollowingLinesFor: 0);
            File.WriteAllBytes(path, Bytes.GetBytes(string.Concat(lines)));
            reverted = true;
        }
        record.Files.Remove(file);
        record.Changes.RemoveAll(c => c.File == file);
        if (record.Files.Count == 0) Delete(clone);
        else Write(clone, record);
        return reverted;
    }

    private static (int Line, int Column)? Rename(List<string> lines, int line, int column, string from, string to, int FollowingLinesFor = FollowingLines)
    {
        for (var index = line - 1; index < lines.Count && index <= line - 1 + FollowingLinesFor; index++)
        {
            var text = lines[index];
            var start = index == line - 1 ? Math.Min(Math.Max(column - 1, 0), text.Length) : 0;
            for (var at = text.IndexOf(from, start, StringComparison.Ordinal); at >= 0; at = text.IndexOf(from, at + 1, StringComparison.Ordinal))
            {
                var before = at == 0 ? ' ' : text[at - 1];
                var after = at + from.Length < text.Length ? text[at + from.Length] : ' ';
                if (IsIdentifierChar(before) || IsIdentifierChar(after)) continue;
                lines[index] = text[..at] + to + text[(at + from.Length)..];
                return (index + 1, at + 1);
            }
        }
        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '@';

    private static List<string> Lines(string path)
    {
        var text = Bytes.GetString(File.ReadAllBytes(path));
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }
        lines.Add(text[start..]);
        return lines;
    }

    private static (string Container, string Name) Split(string symbol)
    {
        var bare = symbol;
        var paren = bare.IndexOf('(');
        if (paren >= 0) bare = bare[..paren];
        var generic = bare.IndexOf('<');
        if (generic >= 0) bare = bare[..generic];
        bare = bare.Trim();
        var dot = bare.LastIndexOf('.');
        return dot < 0 ? ("", bare) : (bare[..dot], bare[(dot + 1)..]);
    }

    private static string? RecordPath(string clone)
    {
        var git = Path.Combine(clone, ".git");
        return Directory.Exists(git) ? Path.Combine(git, RecordName) : null;
    }

    private static void Write(string clone, ApiUpgradeRecord record)
    {
        if (RecordPath(clone) is { } path) AtomicFile.WriteAllText(path, JsonSerializer.Serialize(record, Json));
    }

    private static void Delete(string clone)
    {
        try { if (RecordPath(clone) is { } path && File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting the API upgrade record of {clone}", ex); }
    }

    private static bool SameContent(string path, string hash)
    {
        try { return File.Exists(path) && Hash(File.ReadAllBytes(path)) == hash; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
