using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectsLayoutTests
{
    [AvaloniaFact]
    public async Task Grid_choice_comes_from_settings_and_toggling_saves_it()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"projects-layout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "settings.json");
            var service = new UserSettingsService(path);
            var git = new GitService();
            var vm = new InstallsViewModel(service, new BasisInstallService(new UnityProjectService(), git), git, new BasisUpdateService(git), new MainWindowViewModel());

            await vm.LoadAsync(new UserSettings { ProjectsGridView = true });

            Assert.True(vm.IsGridView);
            Assert.False(vm.IsListView);
            Assert.Equal(L.Tr("installs.button.listView"), vm.LayoutToggleLabel);
            Assert.False(File.Exists(path));

            vm.ToggleLayoutCommand.Execute(null);
            Assert.True(vm.IsListView);
            Assert.Equal(L.Tr("installs.button.gridView"), vm.LayoutToggleLabel);
            Assert.False(await SavedGridAsync(service, path, expected: false));

            vm.ToggleLayoutCommand.Execute(null);
            Assert.True(await SavedGridAsync(service, path, expected: true));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static async Task<bool> SavedGridAsync(UserSettingsService service, string path, bool expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(path) && (await service.LoadAsync()).ProjectsGridView == expected) return expected;
            await Task.Delay(20);
        }
        return (await service.LoadAsync()).ProjectsGridView;
    }
}
