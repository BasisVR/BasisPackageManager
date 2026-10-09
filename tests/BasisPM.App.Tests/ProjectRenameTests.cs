using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectRenameTests
{
    [AvaloniaFact]
    public async Task Renaming_a_card_saves_the_name_and_clearing_it_shows_the_folder_again()
    {
        Localizer.Instance.SetLanguage("en");
        var settingsPath = Path.Combine(Path.GetTempPath(), $"basispm-rename-{Guid.NewGuid():N}.json");
        try
        {
            var shell = new MainWindowViewModel();
            var git = new GitService();
            var settings = new UserSettingsService(settingsPath);
            var vm = new InstallsViewModel(settings, new BasisInstallService(new UnityProjectService(), git), git, new BasisUpdateService(git), shell);
            var root = Path.Combine(Path.GetTempPath(), "Basis");
            var row = new InstallRow(new BasisInstall { RepoRoot = root, UnityProjectPath = Path.Combine(root, "Basis"), Name = "Basis", HasUnityProject = true });
            vm.Installs.Add(row);

            await vm.RenameAsync(row, "  Studio Game  ");
            Assert.Equal("Studio Game", row.Name);
            Assert.Equal("Studio Game", (await settings.LoadAsync()).InstallAliases[root]);
            Assert.Equal("Renamed the project to Studio Game.", shell.StatusMessage);
            Assert.Contains(shell.PackagesVM.InstallOptions, i => i.DisplayName == "Studio Game");

            await vm.RenameAsync(row, "");
            Assert.Equal("Basis", row.Name);
            Assert.Null(row.Install.Alias);
            Assert.Empty((await settings.LoadAsync()).InstallAliases);

            await vm.RenameAsync(row, "Load Test");
            await vm.RenameAsync(row, "Basis");
            Assert.Null(row.Install.Alias);
            Assert.Equal("Basis", row.Name);
        }
        finally { File.Delete(settingsPath); }
    }
}
