using System.ComponentModel;
using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackagesViewModelTests
{
    [AvaloniaFact]
    public void List_header_names_the_selected_project()
    {
        Localizer.Instance.SetLanguage("en");
        var vm = new MainWindowViewModel().PackagesVM;
        vm.ClearActiveInstall();
        Assert.Equal("Available to install", vm.ListHeaderLabel);

        var raised = new List<string?>();
        PropertyChangedEventHandler track = (_, e) => raised.Add(e.PropertyName);
        vm.PropertyChanged += track;
        vm.SetActiveInstall(new BasisInstall { RepoRoot = "r", UnityProjectPath = "r", Name = "Folder", Alias = "My World" });
        vm.PropertyChanged -= track;
        Assert.Contains(nameof(PackagesViewModel.ListHeaderLabel), raised);
        Assert.Equal("Packages for My World", vm.ListHeaderLabel);

        vm.ShowInstalledOnly = true;
        Assert.Equal("Installed", vm.ListHeaderLabel);
        vm.ShowInstalledOnly = false;

        try
        {
            Localizer.Instance.SetLanguage("de");
            Assert.Equal("Pakete für My World", vm.ListHeaderLabel);
        }
        finally { Localizer.Instance.SetLanguage("en"); }
    }
}
