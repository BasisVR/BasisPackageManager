using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
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
}
