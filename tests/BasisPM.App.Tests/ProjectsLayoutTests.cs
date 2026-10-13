using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
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

    [AvaloniaFact]
    public async Task Grid_card_tools_stay_clear_of_the_remove_button()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"projects-grid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var window = new Window { Width = 1200, Height = 1000, FontFamily = FontFamily.Default };
        try
        {
            var git = new GitService();
            var vm = new InstallsViewModel(new UserSettingsService(Path.Combine(root, "settings.json")), new BasisInstallService(new UnityProjectService(), git), git, new BasisUpdateService(git), new MainWindowViewModel());
            await vm.LoadAsync(new UserSettings { ProjectsGridView = true });
            vm.Installs.Add(new InstallRow(new BasisInstall { RepoRoot = Path.Combine(root, "Basis"), UnityProjectPath = Path.Combine(root, "Basis", "Basis"), Name = "Basis", IsGitRepo = true, HasUnityProject = true, IsBasisCheckout = true }));
            SectionState.Load(null, new UserSettings { ExpandedSections = { ["projects.advanced"] = true } });
            window.Content = new InstallsView { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var remove = window.GetVisualDescendants().OfType<Button>().Single(b => ReferenceEquals(b.Command, vm.RemoveCommand) && b.IsEffectivelyVisible);
            var toolbar = (Grid)remove.Parent!;
            var tools = Assert.Single(toolbar.Children.OfType<StackPanel>()).GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList();
            Assert.Contains(tools, b => ReferenceEquals(b.Command, vm.ChangeBranchCommand));
            Assert.All(tools, tool =>
            {
                var right = tool.TranslatePoint(new Point(tool.Bounds.Width, 0), toolbar)!.Value.X;
                Assert.True(right <= remove.Bounds.X, $"{ToolTip.GetTip(tool)} ends at {right:0} but Remove starts at {remove.Bounds.X:0}");
            });
        }
        finally
        {
            SectionState.Load(null, new UserSettings());
            window.Close();
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
