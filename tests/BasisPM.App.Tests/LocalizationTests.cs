using System.Linq;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
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
}
