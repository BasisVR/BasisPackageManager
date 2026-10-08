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

    [Fact]
    public async Task Refresh_lists_the_projects_server_packages()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"server-packages-{Guid.NewGuid():N}");
        try
        {
            Write(root, "Basis Server/BasisNetworkCore/BasisNetworkCore.csproj", "<Project />");
            Write(root, "Basis Server/Directory.Build.targets", "<Project><Import Project=\"Packages/packages-lock.props\" /></Project>");
            Write(root, "Basis Server/Packages/manifest.json", "{ \"dependencies\": { \"com.test.local\": \"file:../../Shared/com.test.local\" } }");
            Write(root, "Shared/com.test.local/package.json", "{ \"name\": \"com.test.local\", \"version\": \"1.2.0\", \"displayName\": \"Local Test\" }");
            Write(root, "Shared/com.test.local/Code/BasisNetworkServer.asmref", "{ \"reference\": \"BasisNetworkServer\" }");
            var vm = new MainWindowViewModel().ServerVM;
            vm.SetActiveInstall(Install(root));

            await vm.RefreshAsync();

            Assert.True(vm.CanManageServerPackages);
            Assert.False(vm.HasServerPackagesNotice);
            var row = Assert.Single(vm.ServerPackages);
            Assert.Equal("Local Test", row.DisplayName);
            Assert.Equal("1.2.0", row.Version);
            Assert.False(row.IsReady);
            Assert.False(row.CanUpdate);
            Assert.Equal("BasisNetworkServer", row.Info.Assemblies);

            await new ServerPackageService(new GitService()).WriteLockAsync(root);
            await vm.RefreshAsync();
            Assert.True(Assert.Single(vm.ServerPackages).IsReady);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Refresh_explains_a_project_without_a_server()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"server-none-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var vm = new MainWindowViewModel().ServerVM;
            vm.SetActiveInstall(Install(root));

            await vm.RefreshAsync();

            Assert.False(vm.CanManageServerPackages);
            Assert.True(vm.HasServerPackagesNotice);
            Assert.Empty(vm.ServerPackages);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
