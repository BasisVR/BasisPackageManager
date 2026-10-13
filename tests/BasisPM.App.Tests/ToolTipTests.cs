using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BasisPM.App.Localization;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ToolTipTests
{
    private const int MaxWords = 100;
    private static readonly HashSet<string> HoverableControls = new() { "Button", "ToggleButton", "RepeatButton", "SplitButton", "DropDownButton", "HyperlinkButton", "CheckBox", "RadioButton", "ToggleSwitch", "ComboBox", "TextBox", "AutoCompleteBox", "NumericUpDown", "Slider", "Expander", "ListBox" };
    private static readonly HashSet<string> PillClasses = new() { "versionPill", "brandBadge", "unofficialBadge", "interactive" };
    private static readonly Regex LocTr = new(@"^\{loc:Tr\s+([^}\s]+)\s*\}$"), KeyLiteral = new(@"""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""");
    internal static readonly string AppSource = FindAppSource();

    [Fact]
    public void Every_hoverable_control_has_a_tooltip()
    {
        var missing = Views().SelectMany(v => v.Xaml.Descendants().Where(e => NeedsToolTip(e) && CanBeHovered(e) && !HasToolTip(e)).Select(e => $"{v.Name}:{((IXmlLineInfo)e).LineNumber} {e.Name.LocalName}")).ToList();
        Assert.True(missing.Count == 0, "These can be hovered but have no ToolTip.Tip:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_tooltip_key_exists_in_english()
    {
        var english = Language("en");
        var missing = ReferencedToolTipKeys().Where(k => !english.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Tooltip keys missing from en.json:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_tooltip_explains_itself_in_100_words_or_less()
    {
        var used = ReferencedToolTipKeys();
        var tooLong = Directory.GetFiles(Path.Combine(AppSource, "Localization", "Languages"), "*.json").SelectMany(f => Language(Path.GetFileNameWithoutExtension(f)).Where(e => (used.Contains(e.Key) || IsToolTipKey(e.Key)) && Words(e.Value) > MaxWords).Select(e => $"{Path.GetFileName(f)} {e.Key}: {Words(e.Value)} words")).ToList();
        Assert.True(tooLong.Count == 0, $"Tooltips over {MaxWords} words:\n" + string.Join("\n", tooLong));
    }

    [AvaloniaFact]
    public void An_expander_shows_its_tooltip_on_the_header_and_not_over_its_content()
    {
        Localizer.Instance.SetLanguage("en");
        var expander = new Expander { Header = "Section", Content = new TextBlock { Text = "Body" }, IsExpanded = true };
        expander.Bind(ToolTip.TipProperty, (IBinding)new TrExtension("nav.installs").ProvideValue(null!));
        var window = new Window { Content = expander };
        try
        {
            expander.ApplyTemplate();
            var header = expander.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "ExpanderHeader");
            Assert.Equal("Projects", ToolTip.GetTip(header));

            Localizer.Instance.SetLanguage("de");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Projekte", ToolTip.GetTip(header));

            var opening = new CancelRoutedEventArgs(ToolTip.ToolTipOpeningEvent);
            expander.RaiseEvent(opening);
            Assert.True(opening.Cancel);
        }
        finally
        {
            Localizer.Instance.SetLanguage("en");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Disabled_controls_still_show_their_tooltip()
    {
        var button = new Button { IsEnabled = false };
        var window = new DialogWindow { Content = button };
        try
        {
            window.Show();
            Assert.True(ToolTip.GetShowOnDisabled(button));
        }
        finally { window.Close(); }
    }

    private sealed class DialogWindow : Window { }

    private static bool NeedsToolTip(XElement e) => HoverableControls.Contains(e.Name.LocalName) || e.Name.LocalName == "Border" && (Classes(e).Overlaps(PillClasses) || (string?)e.Attribute("CornerRadius") == "999");

    private static bool CanBeHovered(XElement e) => e.AncestorsAndSelf().All(a => (string?)a.Attribute("IsHitTestVisible") != "False");

    private static bool HasToolTip(XElement e) => e.Attribute("ToolTip.Tip") is not null || e.Elements().Any(c => c.Name.LocalName == "ToolTip.Tip") || e.Name.LocalName == "ListBox" && e.Elements().Where(c => c.Name.LocalName == "ListBox.ItemTemplate").Elements().Elements().FirstOrDefault() is { } item && HasToolTip(item);

    private static HashSet<string> Classes(XElement e) => ((string?)e.Attribute("Classes") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static bool IsToolTipKey(string key) => key.Contains("tooltip", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Tip", StringComparison.Ordinal);

    private static int Words(string text) => Regex.Matches(text, @"\S+").Count;

    private static HashSet<string> ReferencedToolTipKeys()
    {
        var keys = Views().SelectMany(v => v.Xaml.Descendants().Select(e => (string?)e.Attribute("ToolTip.Tip"))).Select(tip => tip is null ? null : LocTr.Match(tip)).Where(m => m is { Success: true }).Select(m => m!.Groups[1].Value).ToHashSet();
        foreach (var file in Directory.GetFiles(AppSource, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
            keys.UnionWith(KeyLiteral.Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value).Where(IsToolTipKey));
        return keys;
    }

    private static IEnumerable<(string Name, XDocument Xaml)> Views() => Directory.GetFiles(Path.Combine(AppSource, "Views"), "*.axaml").Select(f => (Path.GetFileName(f), XDocument.Load(f, LoadOptions.SetLineInfo)));

    private static Dictionary<string, string> Language(string code)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppSource, "Localization", "Languages", code + ".json")));
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in json.RootElement.GetProperty("entries").EnumerateArray())
            entries[entry.GetProperty("key").GetString() ?? ""] = entry.GetProperty("value").GetString() ?? "";
        return entries;
    }

    private static string FindAppSource([CallerFilePath] string testFile = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(testFile) })
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "BasisPM.App");
                if (File.Exists(Path.Combine(candidate, "BasisPM.App.csproj"))) return candidate;
            }
        throw new DirectoryNotFoundException($"Could not locate src/BasisPM.App starting from {AppContext.BaseDirectory} or {testFile}");
    }
}
