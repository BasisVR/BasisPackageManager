using Avalonia;
using Avalonia.Headless;
using BasisPM.App.Tests;

// Registers a headless Avalonia application so [AvaloniaFact] tests can construct controls,
// evaluate bindings, and reach embedded avares:// assets (the language JSON files).
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace BasisPM.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BasisPM.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
