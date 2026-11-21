using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectFilesTests
{
    private static ProjectFilesViewModel Model(string project) =>
        new(project, "Mine", new GitService(), () => null, _ => Task.FromResult<string?>(null), () => Task.CompletedTask);

    [AvaloniaFact]
    public async Task A_project_without_a_manifest_offers_set_up()
    {
        Localizer.Instance.SetLanguage("en");
        var project = Directory.CreateTempSubdirectory("bpm-ui").FullName;
        try
        {
            var model = Model(project);
            await model.LoadAsync();

            Assert.True(model.ShowSetup);
            Assert.False(model.ShowProject);
            Assert.True(model.SetupGame);
            model.SetupContent = true;
            Assert.False(model.SetupGame);
            Assert.True(model.SetupCommand.CanExecute(null));
        }
        finally { Directory.Delete(project, true); }
    }

    [AvaloniaFact]
    public async Task A_manifest_lists_its_packages_with_their_mounts_and_actions()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Directory.CreateTempSubdirectory("bpm-ui").FullName;
        try
        {
            var project = Path.Combine(root, "Game");
            var local = Path.Combine(root, "Local", "com.mine");
            Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "package.json"), """{ "name": "com.mine", "version": "2.0.0" }""");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), """{ "dependencies": {} }""");
            BpmStore.Save(project, new BpmManifest { Packages = { ["com.mine"] = new BpmManifestPackage { Version = "file:../Local/com.mine" } } }, new BpmLock());
            Assert.True((await new BpmService(new GitService()).ResolveAsync(project)).Ok);

            var model = Model(project);
            await model.LoadAsync();

            Assert.True(model.ShowProject);
            Assert.False(model.HasCheckouts);
            var package = Assert.Single(model.Packages);
            Assert.Equal("com.mine", package.Id);
            Assert.Equal("locked 2.0.0", package.LockedText);
            Assert.Equal("file:../../Local/com.mine", package.MountText);
            Assert.True(package.IsListed);
            Assert.False(package.CanEmbed);
            Assert.Equal("Turn off", package.ToggleText);
            Assert.True(model.LockCommand.CanExecute(null));
            Assert.Contains(model.Messages, m => m.Text.Contains("isn't mounted", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }
}
