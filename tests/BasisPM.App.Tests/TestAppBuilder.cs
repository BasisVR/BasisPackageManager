using Avalonia;
using Avalonia.Headless;
using BasisPM.App.Tests;
using Xunit;

// Registers a headless Avalonia application so [AvaloniaFact] tests can construct controls,
// evaluate bindings, and reach embedded avares:// assets (the language JSON files).
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// The Avalonia headless platform and the shared Localizer singleton hold global state that is not
// safe to drive from xUnit's default per-class parallelism (it caused rare cold-start flakes). The
// suite is tiny and fast, so run it serially for determinism.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BasisPM.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<BasisPM.App.App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
