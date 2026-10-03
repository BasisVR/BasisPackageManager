using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Platform;
using BasisPM.Core.Services;

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
    private const string DefaultLanguage = "en";
    private const string LanguageFolderUri = "avares://BasisPM.App/Localization/Languages";
    private static readonly Uri LanguageFolder = new(LanguageFolderUri, UriKind.Absolute);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // A nested holder decouples singleton construction from this type's field declaration order.
    // The constructor deliberately performs no Avalonia work; assets are loaded on first real use.
    private static class SingletonHolder
    {
        internal static readonly Localizer Value = new();
    }

    public static Localizer Instance => SingletonHolder.Value;

    // code -> (key -> value)
    private readonly Dictionary<string, Dictionary<string, string>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LanguageInfo> _available = new();
    private readonly object _loadLock = new();
    private Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private string _currentCode = DefaultLanguage;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fired after the active language changes; view models re-pull their localized strings here.</summary>
    public event Action<string>? LanguageChanged;

    private Localizer() { }

    /// <summary>Languages discovered from the embedded files, English first then by native name.</summary>
    public IReadOnlyList<LanguageInfo> Available { get { EnsureLoaded(); return _available; } }

    public string CurrentCode => _currentCode;

    /// <summary>Resolve a key: active language → English → the key itself (so gaps are visible, not blank).</summary>
    public string Get(string key)
    {
        EnsureLoaded();
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
        catch (Exception ex) { DiagnosticLog.Write($"Formatting localized string '{key}'", ex); return fmt; }
    }

    public void SetLanguage(string? code)
    {
        EnsureLoaded();
        code = string.IsNullOrWhiteSpace(code) ? DefaultLanguage : code.Trim();
        if (!_tables.TryGetValue(code, out var table))
        {
            code = DefaultLanguage;
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
        // Localizer can be referenced by converters and tests before Avalonia has registered its
        // platform services. Treat that state as "not ready yet" and let EnsureLoaded retry later.
        if (Application.Current is null) return;

        IEnumerable<Uri> assets;
        try
        {
            assets = AssetLoader.GetAssets(LanguageFolder, LanguageFolder);
        }
        catch (Exception ex) { DiagnosticLog.Write($"Enumerating localization assets in {LanguageFolder}", ex); return; }

        foreach (var uri in assets)
        {
            if (!uri.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = AssetLoader.Open(uri, LanguageFolder);
                var file = JsonSerializer.Deserialize<LanguageFile>(stream, JsonOpts);
                if (string.IsNullOrWhiteSpace(file?.Code))
                {
                    continue;
                }

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                if (file!.Entries is { } entries)
                {
                    foreach (var e in entries)
                    {
                        if (!string.IsNullOrEmpty(e.Key)) map[e.Key] = e.Value ?? "";
                    }
                }

                _tables[file.Code] = map;
                _available.Add(new LanguageInfo(file.Code, string.IsNullOrWhiteSpace(file.NativeName) ? file.Code : file.NativeName!));
            }
            catch (Exception ex) { DiagnosticLog.Write($"Loading localization asset {uri}", ex); /* skip a malformed file rather than fail startup */ }
        }

        _available.Sort((a, b) =>
        {
            return string.Equals(a.Code, DefaultLanguage, StringComparison.OrdinalIgnoreCase)? -1 : string.Equals(b.Code, DefaultLanguage, StringComparison.OrdinalIgnoreCase)  ? 1: string.Compare(a.NativeName, b.NativeName, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    // Unit tests can touch the singleton before Avalonia's asset system is initialized. An empty
    // first attempt is not permanent: retry once the application/test host has initialized assets.
    private void EnsureLoaded()
    {
        if (_tables.Count > 0)
        {
            return;
        }

        lock (_loadLock)
        {
            if (_tables.Count > 0)
            {
                return;
            }

            _available.Clear();
            LoadAllTables();
            _fallback = _tables.TryGetValue(DefaultLanguage, out var en) ? en : new(StringComparer.Ordinal);
            _current = _tables.TryGetValue(_currentCode, out var current) ? current : _fallback;
        }
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
