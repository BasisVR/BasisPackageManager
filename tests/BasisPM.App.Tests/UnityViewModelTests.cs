using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class UnityViewModelTests
{
    [AvaloniaFact]
    public void Required_version_note_asks_for_an_install_only_when_that_editor_is_missing()
    {
        Localizer.Instance.SetLanguage("en");
        var unity = new MainWindowViewModel().UnityVM;
        unity.SetRequiredVersion("6000.7.0b4");
        Assert.False(unity.ShowRequiredVersionNote);
        Assert.False(unity.RequiredVersionInstalled);

        unity.InstalledEditors.Add(new InstalledEditor { Version = "6000.4.1f1", Path = "C:/Unity/6000.4.1f1/Editor/Unity.exe" });
        Assert.True(unity.ShowRequiredVersionNote);
        Assert.False(unity.RequiredVersionInstalled);

        var raised = new HashSet<string?>();
        unity.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        unity.InstalledEditors.Add(new InstalledEditor { Version = "6000.7.0b4", Path = "C:/Unity/6000.7.0b4/Editor/Unity.exe" });

        Assert.False(unity.ShowRequiredVersionNote);
        Assert.True(unity.RequiredVersionInstalled);
        Assert.Equal("Your Basis install targets Unity 6000.7.0b4, and it's installed.", unity.RequiredInstalledNote);
        Assert.Contains(nameof(UnityViewModel.ShowRequiredVersionNote), raised);
        Assert.Contains(nameof(UnityViewModel.RequiredVersionInstalled), raised);

        unity.SetRequiredVersion(null);
        Assert.False(unity.ShowRequiredVersionNote);
        Assert.False(unity.RequiredVersionInstalled);
    }

    [AvaloniaFact]
    public void Install_modules_follow_the_selected_release_with_coreclr_ticked_and_choices_kept()
    {
        Localizer.Instance.SetLanguage("en");
        var unity = new MainWindowViewModel().UnityVM;
        var host = UnityBuildModules.HostPlatform(Platform.Current);
        var arch = UnityBuildModules.HostArchitecture(RuntimeInformation.OSArchitecture);
        UnityReleaseRow Row(string version, params string[] modules) => new(new UnityRelease
        {
            Version = version,
            Downloads = { new UnityReleaseDownload { Platform = host, Architecture = arch, Modules = modules.Select(m => new UnityReleaseModule { Id = m }).ToList() } },
        }, false);

        unity.SelectedRelease = Row("6000.7.0b4", "android", "windows-coreclr", "linux-coreclr", "linux-il2cpp", "ios");
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr", "android", "linux-il2cpp" }, unity.ModuleOptions.Select(m => m.Name));
        Assert.Equal(new[] { "windows-coreclr", "linux-coreclr" }, unity.ModuleOptions.Where(m => m.Selected).Select(m => m.Name));

        unity.ModuleOptions.Single(m => m.Name == "linux-coreclr").Selected = false;
        unity.ModuleOptions.Single(m => m.Name == "android").Selected = true;
        unity.SelectedRelease = Row("6000.7.1f1", "android", "windows-coreclr", "linux-coreclr", "mac-coreclr", "linux-il2cpp");
        var ticked = unity.ModuleOptions.Where(m => m.Selected).Select(m => m.Name).ToList();
        Assert.Contains("windows-coreclr", ticked);
        Assert.Contains("android", ticked);
        Assert.DoesNotContain("linux-coreclr", ticked);

        unity.SelectedRelease = Row("6000.3.26f1", "android", "linux-il2cpp");
        Assert.Equal(new[] { "android", "linux-il2cpp" }, unity.ModuleOptions.Select(m => m.Name));
    }
}
