using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;

namespace BasisPM.App.Localization;

/// <summary>
/// Runtime string localization, mirroring the Basis client's own JSON format
/// (<c>{code}.json</c> with <c>code</c> / <c>nativeName</c> / <c>entries[{key,value}]</c>).
/// A single flat key→value store per language; <c>en</c> is the source and fallback.
/// XAML binds through the indexer via the <c>{loc:Tr key}</c> markup extension; view models
/// call <see cref="Get"/> / <see cref="Format"/>. Switching language raises the indexer
/// change so every live binding re-resolves.
/// </summary>
public sealed class Localizer : INotifyPropertyChanged
{
    public static Localizer Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // code -> (key -> value)
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _available = new();
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private string _currentCode = "en";

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fired after the active language changes; view models re-pull their localized strings here.</summary>
    public event Action<string>? LanguageChanged;

    private Localizer()
    {
        LoadAllTables();
        _fallback = _tables.TryGetValue("en", out var en) ? en : new(StringComparer.Ordinal);
        _current = _fallback;
    }

    /// <summary>Languages discovered from the embedded files, English first then by native name.</summary>
    public IReadOnlyList<LanguageInfo> Available => _available;

    public string CurrentCode => _currentCode;

    /// <summary>Resolve a key: active language → English → the key itself (so gaps are visible, not blank).</summary>
    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return key ?? "";
        if (_current.TryGetValue(key, out var v)) return v;
        if (_fallback.TryGetValue(key, out var f)) return f;
        return key;
    }

    /// <summary>Resolve a key and <see cref="string.Format(string,object?[])"/> it with the given arguments.</summary>
    public string Format(string key, params object?[] args)
    {
        var fmt = Get(key);
        if (args is null || args.Length == 0) return fmt;
        try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
        catch { return fmt; }
    }

    public void SetLanguage(string? code)
    {
        code = string.IsNullOrWhiteSpace(code) ? "en" : code.Trim();
        if (!_tables.TryGetValue(code, out var table))
        {
            code = "en";
            table = _fallback;
        }
        if (string.Equals(code, _currentCode, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(table, _current))
            return;

        _currentCode = code;
        _current = table;
        // {loc:Tr} bindings track CurrentCode (through a converter), so this single notification refreshes
        // every localized property in the UI. VM-side consumers listen on LanguageChanged instead.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCode)));
        LanguageChanged?.Invoke(code);
    }

    private void LoadAllTables()
    {
        Uri folder = new("avares://BasisPM.App/Localization/Languages");
        IEnumerable<Uri> assets;
        try { assets = AssetLoader.GetAssets(folder, null); }
        catch { return; }

        foreach (var uri in assets)
        {
            if (!uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = AssetLoader.Open(uri);
                var file = JsonSerializer.Deserialize<LanguageFile>(stream, JsonOpts);
                if (string.IsNullOrWhiteSpace(file?.Code)) continue;

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (file!.Entries is { } entries)
                    foreach (var e in entries)
                        if (!string.IsNullOrEmpty(e.Key)) map[e.Key] = e.Value ?? "";

                _tables[file.Code] = map;
                _available.Add(new LanguageInfo(file.Code, string.IsNullOrWhiteSpace(file.NativeName) ? file.Code : file.NativeName!));
            }
            catch { /* skip a malformed file rather than fail startup */ }
        }

        _available.Sort((a, b) =>
        {
            if (string.Equals(a.Code, "en", StringComparison.OrdinalIgnoreCase)) return -1;
            if (string.Equals(b.Code, "en", StringComparison.OrdinalIgnoreCase)) return 1;
            return string.Compare(a.NativeName, b.NativeName, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private sealed class LanguageFile
    {
        [JsonPropertyName("code")] public string? Code { get; set; }
        [JsonPropertyName("nativeName")] public string? NativeName { get; set; }
        [JsonPropertyName("entries")] public List<Entry>? Entries { get; set; }
    }

    private sealed class Entry
    {
        [JsonPropertyName("key")] public string? Key { get; set; }
        [JsonPropertyName("value")] public string? Value { get; set; }
    }
}

/// <summary>A selectable language: its code (e.g. <c>ja</c>) and native display name (e.g. 日本語).</summary>
public sealed record LanguageInfo(string Code, string NativeName);

/// <summary>Short static entry point for localizing view-model strings: <c>L.Tr("key")</c> / <c>L.Tr("key", arg0, ...)</c>.</summary>
public static class L
{
    public static string Tr(string key) => Localizer.Instance.Get(key);
    public static string Tr(string key, params object?[] args) => Localizer.Instance.Format(key, args);
}
