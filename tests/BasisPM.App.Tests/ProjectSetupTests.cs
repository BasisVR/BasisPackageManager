using Avalonia.Headless.XUnit;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectSetupTests
{
    private const string Settings = "PlayerSettings:\n  companyName: Basis Unity\n  productName: Basis Unity\n  bundleVersion: 2.0.1\n  applicationIdentifier:\n    Android: com.BasisUnity.BasisUnity\n    Standalone: com.Basis-Unity.Basis-Unity\n    iPhone: com.Basis-Unity.Basis-Unity\n  overrideDefaultApplicationIdentifier: 1\n";

    private static string NewProject(string name = "My App")
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", "setup-" + Guid.NewGuid().ToString("N"), name);
        Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
        File.WriteAllText(Path.Combine(root, "ProjectSettings", "ProjectSettings.asset"), Settings);
        return root;
    }

    private static void Delete(string root)
    {
        try { FileRetry.DeleteDirectory(Path.GetDirectoryName(root)!); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [AvaloniaFact]
    public async Task A_new_project_starts_from_its_folder_name_and_saving_applies_ProjectSettings()
    {
        var project = NewProject();
        try
        {
            var model = new ProjectSetupViewModel(project, "My App", _ => false);
            Assert.Equal("My App", model.ProductName);
            Assert.Equal("com.example.myapp", model.Identifier);
            Assert.False(model.IsDirty);
            Assert.Contains(model.Issues, i => i.Message.Contains("placeholder"));
            Assert.All(model.Features, f => Assert.True(f.IsOn));
            model.Identifier = "com.myapp.app";
            model.Features.Single(f => f.Key == "mainMenu").IsOn = false;
            model.AddSettingCommand.Execute(null);
            model.Settings[0].Key = "mainvolume";
            model.Settings[0].Value = "50";
            await model.Save();
            Assert.False(model.IsDirty);
            Assert.True(model.HasInfoStatus);
            var config = BasisConfigService.Read(project)!;
            Assert.Equal("com.myapp.app", config.Identifier);
            Assert.Equal(new Dictionary<string, bool> { ["mainMenu"] = false }, config.Features);
            Assert.Equal(50, config.Settings!["mainvolume"].GetInt32());
            var identity = BasisConfigService.ReadIdentity(File.ReadAllText(BasisConfigService.SettingsPath(project)));
            Assert.Equal("My App", identity.ProductName);
            Assert.Equal("com.myapp.app", identity.Identifiers["Android"]);
        }
        finally { Delete(project); }
    }

    [AvaloniaFact]
    public async Task Saving_with_Unity_open_leaves_ProjectSettings_to_the_editor_and_keeps_unknown_keys()
    {
        var project = NewProject();
        try
        {
            File.WriteAllText(BasisConfigService.ConfigPath(project), "{ \"productName\": \"MyApp\", \"identifier\": \"com.myapp.app\", \"bundle\": { \"icon\": \"icon.png\" }, \"features\": { \"mine\": true } }");
            var model = new ProjectSetupViewModel(project, "My App", _ => true);
            Assert.False(model.IsDirty);
            Assert.Contains(model.Features, f => f.Key == "mine");
            model.Version = "1.0.0";
            await model.Save();
            Assert.Equal(Settings, File.ReadAllText(BasisConfigService.SettingsPath(project)));
            var text = File.ReadAllText(BasisConfigService.ConfigPath(project));
            Assert.Contains("\"bundle\"", text);
            Assert.Contains("\"mine\": true", text);
            Assert.Contains("\"version\": \"1.0.0\"", text);
        }
        finally { Delete(project); }
    }

    [AvaloniaFact]
    public void A_bad_identifier_blocks_saving_and_the_window_builds()
    {
        var project = NewProject();
        try
        {
            var model = new ProjectSetupViewModel(project, "My App", _ => false);
            model.Identifier = "myapp";
            Assert.True(model.HasErrors);
            Assert.False(model.SaveCommand.CanExecute(null));
            model.SuggestIdentifierCommand.Execute(null);
            Assert.Equal("com.example.myapp", model.Identifier);
            Assert.True(model.SaveCommand.CanExecute(null));
            var window = new ProjectSetupWindow { DataContext = model, FontFamily = Avalonia.Media.FontFamily.Default };
            window.Show();
            window.Close();
        }
        finally { Delete(project); }
    }

    [AvaloniaFact]
    public async Task Project_tools_close_the_window_first_and_wait_for_unsaved_changes()
    {
        var project = NewProject();
        try
        {
            var opened = new List<string>();
            var model = new ProjectSetupViewModel(project, "My App", _ => false, openBackup: () => { opened.Add("backup"); return Task.CompletedTask; },
                openStorage: () => { opened.Add("storage"); return Task.CompletedTask; });
            var closed = 0;
            model.CloseRequested += () => closed++;
            Assert.False(model.HasParts);
            Assert.True(model.HasTools);
            Assert.False(model.PartsCommand.CanExecute(null));
            model.ProductName = "Other";
            Assert.False(model.BackupCommand.CanExecute(null));
            await model.Save();
            Assert.True(model.BackupCommand.CanExecute(null));
            model.StorageCommand.Execute(null);
            Assert.Equal(1, closed);
            Assert.Equal(new[] { "storage" }, opened);
        }
        finally { Delete(project); }
    }
}
