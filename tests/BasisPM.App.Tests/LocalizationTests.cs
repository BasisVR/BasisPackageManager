using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using BasisPM.App.Localization;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class LocalizationTests
{
    private static readonly string[] Expected =
        { "en", "ar", "bn", "de", "es", "es-MX", "fr", "hi", "it", "ja", "nl", "pt", "ru", "ur", "zh", "zh-CN", "zh-Hans", "zh-Hant" };

    [AvaloniaFact]
    public void Discovers_every_embedded_language()
    {
        var codes = Localizer.Instance.Available.Select(l => l.Code).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        foreach (var c in Expected)
            Assert.Contains(c, codes);
        Assert.All(Localizer.Instance.Available, l => Assert.False(string.IsNullOrWhiteSpace(l.NativeName)));
    }

    [AvaloniaFact]
    public void Resolves_english_source()
    {
        Localizer.Instance.SetLanguage("en");
        Assert.Equal("Projects", Localizer.Instance.Get("nav.installs"));
        Localizer.Instance.SetLanguage("en");
    }

    [AvaloniaFact]
    public void Unknown_key_returns_the_key()
        => Assert.Equal("does.not.exist", Localizer.Instance.Get("does.not.exist"));

    [AvaloniaFact]
    public void Format_substitutes_arguments()
    {
        // shell.status.copyFailed == "Copy failed: {0}"
        Localizer.Instance.SetLanguage("en");
        Assert.Equal("Copy failed: boom", Localizer.Instance.Format("shell.status.copyFailed", "boom"));
    }

    [AvaloniaFact]
    public void TrBinding_reflects_current_language_and_updates_on_switch()
    {
        var loc = Localizer.Instance;
        loc.SetLanguage("en");

        // Exercise the real mechanism XAML uses: the {loc:Tr nav.installs} markup extension.
        var tb = new TextBlock();
        var binding = (IBinding)new TrExtension("nav.installs").ProvideValue(null!);
        tb.Bind(TextBlock.TextProperty, binding);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Projects", tb.Text);

        // Switching language must update the bound text live.
        loc.SetLanguage("de");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(loc.Get("nav.installs"), tb.Text);
        Assert.Equal("Projekte", tb.Text);
        Assert.NotEqual("Projects", tb.Text);

        loc.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Projects", tb.Text);
    }

    [AvaloniaFact]
    public void Every_language_has_every_english_key_and_no_others()
    {
        var languages = Languages();
        var english = languages["en"].Select(e => e.Key).ToHashSet(System.StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (code, entries) in languages)
        {
            var keys = entries.Select(e => e.Key).ToList();
            problems.AddRange(keys.GroupBy(k => k, System.StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => $"{code} lists {g.Key} {g.Count()} times"));
            problems.AddRange(english.Except(keys, System.StringComparer.Ordinal).Select(k => $"{code} is missing {k}"));
            problems.AddRange(keys.Except(english, System.StringComparer.Ordinal).Select(k => $"{code} has {k}, which en.json doesn't"));
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [AvaloniaFact]
    public void Every_translation_keeps_the_english_placeholders_and_formats()
    {
        var languages = Languages();
        var english = languages["en"].DistinctBy(e => e.Key).ToDictionary(e => e.Key, e => e.Value, System.StringComparer.Ordinal);
        var args = Enumerable.Repeat<object?>("x", 10).ToArray();
        var problems = new List<string>();
        foreach (var (code, entries) in languages)
            foreach (var (key, value) in entries)
            {
                if (!english.TryGetValue(key, out var source)) continue;
                var expected = Placeholders(source);
                if (!expected.SequenceEqual(Placeholders(value))) problems.Add($"{code} {key} uses {string.Join(" ", Placeholders(value))} instead of {string.Join(" ", expected)}");
                else if (expected.Count > 0 && !Formats(value, args)) problems.Add($"{code} {key} can't be formatted: {value}");
            }
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void Every_key_the_app_uses_exists_in_english()
    {
        var root = ToolTipTests.AppSource;
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(root, "Localization", "Languages", "en.json")));
        var english = json.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToHashSet(System.StringComparer.Ordinal);
        var views = System.IO.Directory.GetFiles(System.IO.Path.Combine(root, "Views"), "*.axaml")
            .SelectMany(f => Regex.Matches(System.IO.File.ReadAllText(f), @"\{loc:Tr\s+([^}\s]+)\s*\}").Select(m => (File: f, Key: m.Groups[1].Value))).ToList();
        var code = System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories)
            .Where(f => !f.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar).Any(part => part is "obj" or "bin"))
            .SelectMany(f => Regex.Matches(System.IO.File.ReadAllText(f), @"L\.Tr\(\s*""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""").Select(m => (File: f, Key: m.Groups[1].Value))).ToList();
        Assert.NotEmpty(views);
        Assert.NotEmpty(code);
        var missing = views.Concat(code).Where(u => !english.Contains(u.Key)).Select(u => $"{System.IO.Path.GetFileName(u.File)}: {u.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "Keys used by the app but missing from en.json:\n" + string.Join("\n", missing));
    }

    private static Dictionary<string, List<(string Key, string Value)>> Languages()
    {
        var folder = new System.Uri("avares://BasisPM.App/Localization/Languages");
        var languages = new Dictionary<string, List<(string Key, string Value)>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var uri in AssetLoader.GetAssets(folder, folder).Where(u => u.AbsolutePath.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = AssetLoader.Open(uri, folder);
            using var json = JsonDocument.Parse(stream);
            languages[json.RootElement.GetProperty("code").GetString() ?? uri.AbsolutePath] = json.RootElement.GetProperty("entries").EnumerateArray().Select(e => (e.GetProperty("key").GetString() ?? "", e.GetProperty("value").GetString() ?? "")).ToList();
        }
        return languages;
    }

    private static List<string> Placeholders(string text) => Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Distinct().Order(System.StringComparer.Ordinal).ToList();

    private static bool Formats(string text, object?[] args)
    {
        try { _ = string.Format(CultureInfo.InvariantCulture, text, args); return true; }
        catch (System.FormatException) { return false; }
    }
}
