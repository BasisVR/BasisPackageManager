using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class SectionStateTests
{
    [AvaloniaFact]
    public async Task Sections_come_back_the_way_the_user_left_them()
    {
        Localizer.Instance.SetLanguage("en");
        var path = Path.Combine(Path.GetTempPath(), $"basispm-sections-{Guid.NewGuid():N}.json");
        var service = new UserSettingsService(path);
        try
        {
            SectionState.Load(service, new UserSettings { ExpandedSections = { ["server.runtime"] = false } });
            var shell = new MainWindowViewModel();

            var view = Show(shell);
            Assert.False(Section(view, "server.runtime").IsExpanded);
            Assert.True(Section(view, "server.packages").IsExpanded);

            Section(view, "server.runtime").IsExpanded = true;
            Section(view, "server.steam").IsExpanded = false;
            var saved = await SavedAsync(service, s => s.ExpandedSections.Count == 2);
            Assert.True(saved.ExpandedSections["server.runtime"]);
            Assert.False(saved.ExpandedSections["server.steam"]);

            // Every tab switch builds a fresh view; it has to come back the way it was left.
            var again = Show(shell);
            Assert.True(Section(again, "server.runtime").IsExpanded);
            Assert.False(Section(again, "server.steam").IsExpanded);
            Assert.True(Section(again, "server.config").IsExpanded);

            SectionState.Clear();
            Assert.True(Section(Show(shell), "server.steam").IsExpanded);
        }
        finally
        {
            SectionState.Load(null, new UserSettings());
            File.Delete(path);
        }
    }

    private static ServerView Show(MainWindowViewModel shell)
    {
        var view = new ServerView { DataContext = shell.ServerVM };
        _ = new Window { Content = view };
        return view;
    }

    private static Expander Section(Control view, string key) =>
        view.GetLogicalDescendants().OfType<Expander>().Single(e => SectionState.GetKey(e) == key);

    private static async Task<UserSettings> SavedAsync(UserSettingsService service, Func<UserSettings, bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var settings = await service.LoadAsync();
            if (done(settings)) return settings;
            await Task.Delay(25);
        }
        return await service.LoadAsync();
    }
}
