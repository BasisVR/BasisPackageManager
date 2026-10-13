using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class AdvancedOptionsTests
{
    [AvaloniaFact]
    public void Every_page_with_developer_tools_has_its_own_advanced_toggle_that_starts_off()
    {
        Localizer.Instance.SetLanguage("en");
        SectionState.Load(null, new UserSettings());
        try
        {
            var shell = new MainWindowViewModel();
            var pages = new (Control View, string Key)[]
            {
                (new PackagesView { DataContext = shell.PackagesVM }, "packages.advanced"),
                (new InstallsView { DataContext = shell.InstallsVM }, "projects.advanced"),
                (new ServerView { DataContext = shell.ServerVM }, "server.advanced"),
                (new UnityView { DataContext = shell.UnityVM }, "unity.advanced"),
                (new SettingsView { DataContext = shell.SettingsVM }, "settings.advanced"),
            };
            foreach (var (view, key) in pages)
            {
                var toggle = Toggle(Show(view));
                Assert.Equal(key, SectionState.GetKey(toggle));
                Assert.False(toggle.IsChecked);
            }
        }
        finally { SectionState.Load(null, new UserSettings()); }
    }

    [AvaloniaFact]
    public async Task Package_developer_tools_appear_with_advanced_and_it_is_remembered()
    {
        Localizer.Instance.SetLanguage("en");
        var path = Path.Combine(Path.GetTempPath(), $"basispm-advanced-{Guid.NewGuid():N}.json");
        var service = new UserSettingsService(path);
        SectionState.Load(service, new UserSettings());
        try
        {
            var shell = new MainWindowViewModel();
            var view = Show(new PackagesView { DataContext = shell.PackagesVM });
            var tools = (Control)view.GetLogicalDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, shell.PackagesVM.NewPackageCommand)).GetLogicalParent()!;
            Assert.False(tools.IsVisible);

            Toggle(view).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(tools.IsVisible);
            var saved = await SavedAsync(service, s => s.ExpandedSections.ContainsKey("packages.advanced"));
            Assert.True(saved.ExpandedSections["packages.advanced"]);

            var again = Show(new PackagesView { DataContext = shell.PackagesVM });
            Assert.True(Toggle(again).IsChecked);
        }
        finally
        {
            SectionState.Load(null, new UserSettings());
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void Server_packages_and_developer_settings_wait_for_advanced()
    {
        Localizer.Instance.SetLanguage("en");
        SectionState.Load(null, new UserSettings());
        try
        {
            var shell = new MainWindowViewModel();
            var server = Show(new ServerView { DataContext = shell.ServerVM });
            var settings = Show(new SettingsView { DataContext = shell.SettingsVM });
            Assert.False(Section(server, "server.packages").IsVisible);
            Assert.False(Section(settings, "settings.cloneCache").IsVisible);
            Assert.False(Section(settings, "settings.detected").IsVisible);
            Assert.True(Section(settings, "settings.general").IsVisible);

            Toggle(server).IsChecked = true;
            Toggle(settings).IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(Section(server, "server.packages").IsVisible);
            Assert.True(Section(settings, "settings.cloneCache").IsVisible);
            Assert.True(Section(settings, "settings.detected").IsVisible);
        }
        finally { SectionState.Load(null, new UserSettings()); }
    }

    private static Control Show(Control view)
    {
        _ = new Window { Content = view };
        Dispatcher.UIThread.RunJobs();
        return view;
    }

    private static ToggleButton Toggle(Control view) => view.GetLogicalDescendants().OfType<ToggleButton>().Single(t => t.Name == "AdvancedToggle");

    private static Expander Section(Control view, string key) => view.GetLogicalDescendants().OfType<Expander>().Single(e => SectionState.GetKey(e) == key);

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
