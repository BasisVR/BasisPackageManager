using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UserSettingsServiceTests
{
    [Fact]
    public async Task Save_then_Load_round_trips_all_fields()
    {
        using var t = new TempDir();
        var svc = new UserSettingsService(t.Combine("settings.json"));
        var settings = new UserSettings
        {
            Installs = { @"C:\Basis", @"D:\Other" },
            CatalogUrl = "https://example.com/catalog.json",
            ShowLocalChanges = true,
            PrereleaseUpdates = true,
            SeenAnnouncementIds = { "a", "b" },
            ManualEditors = { new ManualUnityEditor { Version = "6000.0.30f1", Path = @"C:\Unity\6000.0.30f1\Editor\Unity.exe" } },
        };
        settings.InstallAliases[@"C:\Basis"] = "My Basis";

        await svc.SaveAsync(settings);
        var loaded = await svc.LoadAsync();

        Assert.Equal(new[] { @"C:\Basis", @"D:\Other" }, loaded.Installs);
        Assert.Equal("https://example.com/catalog.json", loaded.CatalogUrl);
        Assert.True(loaded.ShowLocalChanges);
        Assert.True(loaded.PrereleaseUpdates);
        Assert.Equal("My Basis", loaded.InstallAliases[@"C:\Basis"]);
        Assert.Equal(new[] { "a", "b" }, loaded.SeenAnnouncementIds);
        var editor = Assert.Single(loaded.ManualEditors);
        Assert.Equal("6000.0.30f1", editor.Version);
        Assert.Equal(@"C:\Unity\6000.0.30f1\Editor\Unity.exe", editor.Path);
        Assert.True(editor.ToInstalledEditor().IsManual);
    }

    [Fact]
    public async Task Load_tolerates_a_partial_manual_editor_entry()
    {
        // A hand-edited entry missing "path" must not blow up the whole settings load — the manual
        // editor is a plain, non-required model precisely so a bad entry degrades to empty strings.
        using var t = new TempDir();
        var path = t.WriteFile("settings.json",
            "{ \"installs\": [\"C:\\\\Basis\"], \"manualEditors\": [ { \"version\": \"6000.0.30f1\" } ] }");
        var svc = new UserSettingsService(path);

        var loaded = await svc.LoadAsync();

        Assert.Equal(new[] { @"C:\Basis" }, loaded.Installs);
        var editor = Assert.Single(loaded.ManualEditors);
        Assert.Equal("6000.0.30f1", editor.Version);
        Assert.Equal("", editor.Path);
    }

    [Fact]
    public async Task Load_returns_defaults_when_file_missing()
    {
        using var t = new TempDir();
        var svc = new UserSettingsService(t.Combine("does-not-exist.json"));

        var loaded = await svc.LoadAsync();

        Assert.Empty(loaded.Installs);
        Assert.Equal("", loaded.CatalogUrl);
    }

    [Fact]
    public async Task Load_returns_defaults_on_corrupt_json()
    {
        using var t = new TempDir();
        var path = t.WriteFile("settings.json", "{ this is not valid json ");
        var svc = new UserSettingsService(path);

        var loaded = await svc.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Empty(loaded.Installs);
    }

    [Fact]
    public async Task Save_creates_the_parent_directory()
    {
        using var t = new TempDir();
        var path = t.Combine("nested/deeper/settings.json");
        var svc = new UserSettingsService(path);

        await svc.SaveAsync(new UserSettings());

        Assert.True(File.Exists(path));
    }

    [Fact]
    public void SettingsPath_reflects_the_override()
    {
        using var t = new TempDir();
        var path = t.Combine("settings.json");
        Assert.Equal(path, new UserSettingsService(path).SettingsPath);
    }
}
