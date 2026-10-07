using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ServerViewModelTests
{
    [Fact]
    public async Task Refresh_keeps_unsaved_config_edits_for_the_same_project_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"server-{Guid.NewGuid():N}");
        var other = root + "-other";
        try
        {
            WriteConfig(root, "Old");
            WriteConfig(other, "Old");
            var vm = new MainWindowViewModel().ServerVM;
            vm.SetActiveInstall(Install(root));
            ServerName(vm).Value = "Edited";

            await vm.RefreshAsync();
            Assert.Equal("Edited", ServerName(vm).Value);

            vm.SaveConfigCommand.Execute(null);
            WriteConfig(root, "External");
            await vm.RefreshAsync();
            Assert.Equal("External", ServerName(vm).Value);

            ServerName(vm).Value = "Edited again";
            vm.SetActiveInstall(Install(other));
            Assert.Equal("Old", ServerName(vm).Value);
        }
        finally
        {
            foreach (var dir in new[] { root, other })
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private static ServerConfigFieldRow ServerName(ServerViewModel vm) => vm.ConfigFields.Single(x => x.Name == "ServerName");

    private static void WriteConfig(string root, string serverName)
    {
        var config = new BasisServerService().GetPaths(root).ConfigFile;
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, $"<Configuration><ServerName>{serverName}</ServerName><SetPort>4296</SetPort></Configuration>");
    }

    private static BasisInstall Install(string root) => new() { RepoRoot = root, UnityProjectPath = root, Name = Path.GetFileName(root) };
}
