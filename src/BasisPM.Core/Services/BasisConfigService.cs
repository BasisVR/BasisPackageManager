using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public static class BasisConfigService
{
    public const string FileName = "basis.config.json";
    public const string ProjectSettingsFile = "ProjectSettings/ProjectSettings.asset";

    public static readonly string[] IdentifierPlatforms = { "Standalone", "Android", "iPhone" };

    public static readonly IReadOnlyDictionary<string, string> KnownFeatures = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["mainMenu"] = "the Basis menu, its hotbar and the prompts that open it",
        ["nameplates"] = "nameplates and chat bubbles over other players",
        ["loadingBar"] = "the loading bar shown while content downloads",
        ["joinLeaveMessages"] = "the messages when someone joins or leaves",
        ["onScreenControls"] = "the touch controls on phones and tablets",
        ["microphone"] = "the microphone; nobody hears this player",
    };

    private static readonly string[] BasisNames = { "Basis Unity", "Basis", "BasisUnity", "Basis-Unity" };

    private static readonly string[] BasisIdentifiers =
    {
        "com.BasisUnity.BasisUnity", "com.Basis-Unity.Basis-Unity", "org.BasisUnity.BasisUnity", "org.Basis-Unity.Basis-Unity",
    };

    private static readonly Regex Segment = new("^[A-Za-z][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex VersionPattern = new(@"^\d+(\.\d+){0,3}([-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    public static string ConfigPath(string projectPath) => Path.Combine(projectPath, FileName);

    public static string SettingsPath(string projectPath) => Path.Combine(projectPath, "ProjectSettings", "ProjectSettings.asset");

    public static bool Exists(string projectPath) => File.Exists(ConfigPath(projectPath));

    public static BasisConfig? Read(string projectPath)
    {
        var path = ConfigPath(projectPath);
        if (!File.Exists(path)) return null;
        BasisConfig? config;
        try { config = JsonSerializer.Deserialize<BasisConfig>(File.ReadAllText(path), BpmStore.Json); }
        catch (JsonException ex) { throw new InvalidDataException($"{path} isn't valid JSON ({ex.Message}). Fix it, then try again.", ex); }
        return config ?? throw new InvalidDataException($"{path} doesn't hold a Basis project config. Fix or delete it, then try again.");
    }

    public static BasisConfig? TryRead(string projectPath)
    {
        try { return Read(projectPath); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return null; }
    }

    public static void Write(string projectPath, BasisConfig config)
    {
        lock (BpmStore.Gate)
        {
            if (config.Features is { Count: 0 }) config.Features = null;
            else if (config.Features is { } features) config.Features = new Dictionary<string, bool>(features.OrderBy(f => f.Key, StringComparer.Ordinal));
            if (config.Settings is { Count: 0 }) config.Settings = null;
            else if (config.Settings is { } settings) config.Settings = new Dictionary<string, JsonElement>(settings.OrderBy(s => s.Key, StringComparer.Ordinal));
            var path = ConfigPath(projectPath);
            var text = JsonSerializer.Serialize(config, BpmStore.Json) + "\n";
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal)) return;
            AtomicFile.WriteAllText(path, text);
        }
    }

    public static BasisConfig Update(string projectPath, Action<BasisConfig> change)
    {
        lock (BpmStore.Gate)
        {
            var config = Read(projectPath) ?? Starter(projectPath);
            change(config);
            Write(projectPath, config);
            return config;
        }
    }

    public static BasisConfig Starter(string projectPath, string? productName = null)
    {
        var settings = SettingsPath(projectPath);
        var identity = File.Exists(settings) ? ReadIdentity(File.ReadAllText(settings)) : null;
        var name = Blank(productName) ?? identity?.ProductName;
        if (name is null || IsBasisName(name)) name = Path.GetFileName(Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var company = identity?.CompanyName is { } c && !IsBasisName(c) ? c : null;
        var identifier = identity?.Identifiers.GetValueOrDefault("Standalone") is { } id && !IsBasisIdentifier(id) ? id : SuggestIdentifier(company, name);
        return new BasisConfig { ProductName = name, CompanyName = company, Identifier = identifier, Version = Blank(identity?.Version) ?? "0.1.0" };
    }

    public static string SuggestIdentifier(string? company, string productName) => $"com.{Slug(company) ?? "example"}.{Slug(productName) ?? "app"}";

    public static bool IsBasisName(string? name) => name is not null && BasisNames.Any(b => string.Equals(b, name.Trim(), StringComparison.OrdinalIgnoreCase));

    public static bool IsBasisIdentifier(string? identifier) => identifier is not null && BasisIdentifiers.Any(b => string.Equals(b, identifier.Trim(), StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<BpmIssue> Validate(BasisConfig config)
    {
        var issues = new List<BpmIssue>();
        if (Blank(config.ProductName) is null) issues.Add(new(BpmIssueLevel.Warning, "productName", "productName is empty, so the app keeps whatever name ProjectSettings has."));
        else if (IsBasisName(config.ProductName)) issues.Add(new(BpmIssueLevel.Warning, "productName", $"productName is still \"{config.ProductName}\". Apps with the same company and product name share one save folder, so they read each other's settings."));
        if (IsBasisName(config.CompanyName)) issues.Add(new(BpmIssueLevel.Warning, "companyName", $"companyName is still \"{config.CompanyName}\". Use your own, so saved settings don't mix with Basis or other apps."));
        if (Blank(config.Identifier) is not { } identifier) issues.Add(new(BpmIssueLevel.Warning, "identifier", "identifier is empty. Set one, such as com.myapp.app, so this app installs next to others instead of over them."));
        else
        {
            if (IdentifierProblem(identifier) is { } problem) issues.Add(new(BpmIssueLevel.Error, "identifier", $"identifier \"{identifier}\" {problem}"));
            else if (identifier.Contains('-')) issues.Add(new(BpmIssueLevel.Warning, "identifier", $"identifier \"{identifier}\" has a '-'. Android doesn't allow one; use '_' or leave it out."));
            if (IsBasisIdentifier(identifier)) issues.Add(new(BpmIssueLevel.Warning, "identifier", $"identifier is still Basis's own ({identifier}). Two apps with one identifier install over each other and share saved settings."));
            else if (identifier.StartsWith("com.example.", StringComparison.OrdinalIgnoreCase)) issues.Add(new(BpmIssueLevel.Warning, "identifier", $"identifier {identifier} is a placeholder. Change com.example to a domain or name that is yours."));
        }
        if (Blank(config.ProductName) is { } product && !IsBasisName(product) || Blank(config.CompanyName) is { } company && !IsBasisName(company))
            issues.Add(new(BpmIssueLevel.Info, "server", "A Basis server only lets in clients whose company and product name match its CompanyName and ProductName in config.xml. Set the same pair on the servers this app joins."));
        if (Blank(config.Version) is { } version && !VersionPattern.IsMatch(version)) issues.Add(new(BpmIssueLevel.Warning, "version", $"version \"{version}\" isn't numbers and dots, such as 1.2.0. Stores and Android expect that shape."));
        foreach (var (key, value) in config.Settings ?? new Dictionary<string, JsonElement>())
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number or JsonValueKind.String))
                issues.Add(new(BpmIssueLevel.Error, "settings." + key, $"settings.{key} must be true, false, a number or text."));
        foreach (var key in (config.Features?.Keys ?? Enumerable.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k) && !KnownFeatures.ContainsKey(k)))
            issues.Add(new(BpmIssueLevel.Info, "features." + key, KnownFeatures.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) is { } close
                ? $"features.{key} isn't a Basis feature; did you mean {close}? Names are case-sensitive."
                : $"features.{key} isn't one of the Basis features this version knows ({string.Join(", ", KnownFeatures.Keys)}). A package of your own can still read it."));
        foreach (var key in (config.Features?.Keys ?? Enumerable.Empty<string>()).Concat(config.Settings?.Keys ?? Enumerable.Empty<string>()).Where(k => string.IsNullOrWhiteSpace(k)))
            issues.Add(new(BpmIssueLevel.Error, key, "A feature or setting has an empty name."));
        return issues;
    }

    public static string? IdentifierProblem(string identifier)
    {
        var parts = identifier.Split('.');
        if (parts.Length < 2) return "needs at least two parts separated by dots, such as com.example.app.";
        if (parts.Any(p => p.Length == 0)) return "has an empty part between dots.";
        if (parts.FirstOrDefault(p => !Segment.IsMatch(p)) is { } bad) return $"has the part \"{bad}\"; each part starts with a letter and holds only letters, digits, '_' or '-'.";
        return identifier.Length > 150 ? "is longer than 150 characters." : null;
    }

    public static IReadOnlyList<BasisConfigDrift> Drift(string projectSettingsText, BasisConfig config)
    {
        var identity = ReadIdentity(projectSettingsText);
        var drift = new List<BasisConfigDrift>();
        void Compare(string field, string? want, string? have)
        {
            if (Blank(want) is { } w && !string.Equals(w, have, StringComparison.Ordinal)) drift.Add(new(field, w, have));
        }
        Compare("productName", config.ProductName, identity.ProductName);
        Compare("companyName", config.CompanyName, identity.CompanyName);
        Compare("version", config.Version, identity.Version);
        foreach (var platform in IdentifierPlatforms) Compare("identifier (" + platform + ")", config.Identifier, identity.Identifiers.GetValueOrDefault(platform));
        return drift;
    }

    public static IReadOnlyList<BpmIssue> Check(string projectPath)
    {
        BasisConfig? config;
        try { config = Read(projectPath); }
        catch (InvalidDataException ex) { return new[] { new BpmIssue(BpmIssueLevel.Error, FileName, ex.Message) }; }
        if (config is null) return Array.Empty<BpmIssue>();
        var issues = Validate(config).Select(i => i with { Message = $"{FileName}: {i.Message}" }).ToList();
        var settings = SettingsPath(projectPath);
        if (File.Exists(settings) && Drift(File.ReadAllText(settings), config) is { Count: > 0 } drift)
            issues.Add(new(BpmIssueLevel.Warning, FileName, $"ProjectSettings doesn't match {FileName} yet ({string.Join(", ", drift.Select(d => d.Field))}). Run 'app-config apply' with Unity closed, or open the project and Basis applies it."));
        return issues;
    }

    public static BpmResult ApplyToProject(string projectPath, bool unityOpen)
    {
        BasisConfig? config;
        try { config = Read(projectPath); }
        catch (InvalidDataException ex) { return Failed(FileName, ex.Message); }
        if (config is null) return Failed(FileName, $"{projectPath} has no {FileName}. Run 'app-config init' first.");
        var errors = Validate(config).Where(i => i.Level == BpmIssueLevel.Error).ToList();
        if (errors.Count > 0) return new BpmResult(false, errors, Array.Empty<string>());
        var path = SettingsPath(projectPath);
        if (!File.Exists(path)) return Failed(ProjectSettingsFile, $"{projectPath} has no {ProjectSettingsFile} yet. Open it in Unity once, then apply again.");
        var text = File.ReadAllText(path);
        var drift = Drift(text, config);
        if (drift.Count == 0) return new BpmResult(true, Array.Empty<BpmIssue>(), new[] { $"ProjectSettings already matches {FileName}." });
        if (unityOpen) return Failed(ProjectSettingsFile, $"Unity has this project open and would write its own copy of ProjectSettings over the change. Basis applies {FileName} when the editor notices it changed; otherwise close Unity and apply again.");
        AtomicFile.WriteAllText(path, Apply(text, config));
        return new BpmResult(true, Array.Empty<BpmIssue>(), new[] { $"Applied {FileName} to {ProjectSettingsFile}: {string.Join(", ", drift.Select(d => $"{d.Field} {d.ProjectSettings ?? "(none)"} -> {d.Config}"))}." });
    }

    public static BasisIdentity ReadIdentity(string text)
    {
        string? product = null, company = null, version = null;
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        var inIdentifiers = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (inIdentifiers)
            {
                if (line.StartsWith("    ", StringComparison.Ordinal) && line.IndexOf(':') is var colon and > 0)
                {
                    identifiers[line[..colon].Trim()] = Scalar(line[(colon + 1)..]);
                    continue;
                }
                inIdentifiers = false;
            }
            if (Field(line, "productName") is { } p) product = p;
            else if (Field(line, "companyName") is { } c) company = c;
            else if (Field(line, "bundleVersion") is { } v) version = v;
            else if (line.StartsWith("  applicationIdentifier:", StringComparison.Ordinal)) inIdentifiers = line.TrimEnd().EndsWith(':');
        }
        return new BasisIdentity(product, company, version, identifiers);
    }

    public static string Apply(string text, BasisConfig config)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        void SetField(string name, string? value)
        {
            if (Blank(value) is not { } v) return;
            var index = lines.FindIndex(l => Field(l, name) is not null);
            if (index >= 0) lines[index] = $"  {name}: {Yaml(v)}";
        }
        SetField("productName", config.ProductName);
        SetField("companyName", config.CompanyName);
        SetField("bundleVersion", config.Version);
        if (Blank(config.Identifier) is { } identifier)
        {
            var start = lines.FindIndex(l => l.StartsWith("  applicationIdentifier:", StringComparison.Ordinal));
            if (start >= 0)
            {
                var end = start + 1;
                var present = new HashSet<string>(StringComparer.Ordinal);
                if (!lines[start].TrimEnd().EndsWith(':')) lines[start] = "  applicationIdentifier:";
                else
                {
                    for (; end < lines.Count && lines[end].StartsWith("    ", StringComparison.Ordinal) && lines[end].IndexOf(':') is var colon and > 0; end++)
                    {
                        var platform = lines[end][..colon].Trim();
                        present.Add(platform);
                        lines[end] = $"    {platform}: {Yaml(identifier)}";
                    }
                }
                var missing = IdentifierPlatforms.Where(p => !present.Contains(p)).Select(p => $"    {p}: {Yaml(identifier)}").ToList();
                lines.InsertRange(end, missing);
                var all = lines.Skip(start + 1).TakeWhile(l => l.StartsWith("    ", StringComparison.Ordinal)).OrderBy(l => l.Trim(), StringComparer.Ordinal).ToList();
                lines.RemoveRange(start + 1, all.Count);
                lines.InsertRange(start + 1, all);
            }
            var overrideIndex = lines.FindIndex(l => Field(l, "overrideDefaultApplicationIdentifier") is not null);
            if (overrideIndex >= 0) lines[overrideIndex] = "  overrideDefaultApplicationIdentifier: 1";
        }
        return string.Join(newline, lines);
    }

    public static string? Get(BasisConfig config, string key)
    {
        var (section, name) = SplitKey(key);
        return section switch
        {
            "productName" => config.ProductName,
            "companyName" => config.CompanyName,
            "identifier" => config.Identifier,
            "version" => config.Version,
            "features" => config.Features?.TryGetValue(name!, out var on) == true ? (on ? "true" : "false") : null,
            "settings" => config.Settings?.TryGetValue(name!, out var value) == true ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null,
            _ => throw new ArgumentException(UnknownKey(key)),
        };
    }

    public static void Set(BasisConfig config, string key, string? value)
    {
        var (section, name) = SplitKey(key);
        var text = value?.Trim();
        switch (section)
        {
            case "productName": config.ProductName = Blank(text); break;
            case "companyName": config.CompanyName = Blank(text); break;
            case "identifier": config.Identifier = Blank(text); break;
            case "version": config.Version = Blank(text); break;
            case "features":
                config.Features ??= new Dictionary<string, bool>(StringComparer.Ordinal);
                if (text is null) config.Features.Remove(name!);
                else config.Features[name!] = ParseSwitch(text) ?? throw new ArgumentException($"features.{name} takes on or off, not \"{text}\".");
                break;
            case "settings":
                config.Settings ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                if (text is null) config.Settings.Remove(name!);
                else config.Settings[name!] = SettingValue(text);
                break;
            default: throw new ArgumentException(UnknownKey(key));
        }
    }

    public static bool? ParseSwitch(string text) => text.Trim().ToLowerInvariant() switch
    {
        "true" or "on" or "yes" or "1" or "enabled" => true,
        "false" or "off" or "no" or "0" or "disabled" => false,
        _ => null,
    };

    public static JsonElement SettingValue(string text)
    {
        if (text is "true" or "false" || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            using var parsed = JsonDocument.Parse(text);
            return parsed.RootElement.Clone();
        }
        return JsonSerializer.SerializeToElement(text);
    }

    private static (string Section, string? Name) SplitKey(string key)
    {
        var trimmed = key.Trim();
        var dot = trimmed.IndexOf('.');
        var head = dot < 0 ? trimmed : trimmed[..dot];
        var name = dot < 0 ? null : trimmed[(dot + 1)..].Trim();
        var section = head.ToLowerInvariant() switch
        {
            "productname" or "name" or "product" => "productName",
            "companyname" or "company" => "companyName",
            "identifier" or "id" or "bundleid" => "identifier",
            "version" => "version",
            "features" or "feature" => "features",
            "settings" or "setting" => "settings",
            _ => throw new ArgumentException(UnknownKey(key)),
        };
        if (section is "features" or "settings" ? string.IsNullOrEmpty(name) : name is not null) throw new ArgumentException(UnknownKey(key));
        return (section, name);
    }

    private static string UnknownKey(string key) => $"\"{key}\" isn't a config key. Use productName, companyName, identifier, version, features.<name> or settings.<key>.";

    private static string? Field(string line, string name)
    {
        var prefix = "  " + name + ":";
        if (!line.StartsWith(prefix, StringComparison.Ordinal) || line.Length > prefix.Length && line[prefix.Length] != ' ') return null;
        return Scalar(line[prefix.Length..]);
    }

    private static string Scalar(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
        return value;
    }

    public static string Yaml(string value)
    {
        var plain = value.Length > 0 && value.Trim() == value && !value.Contains(": ", StringComparison.Ordinal) && !value.Contains(" #", StringComparison.Ordinal) && !value.EndsWith(':')
            && "-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) < 0 && !value.Any(char.IsControl);
        return plain ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }

    private static string? Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var builder = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9') builder.Append(char.ToLowerInvariant(c));
        var slug = builder.ToString().TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return slug.Length == 0 ? null : slug;
    }

    private static BpmResult Failed(string subject, string message) => new(false, new[] { new BpmIssue(BpmIssueLevel.Error, subject, message) }, Array.Empty<string>());

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
