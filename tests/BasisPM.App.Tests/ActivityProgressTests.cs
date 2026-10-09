using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.App.Views;
using BasisPM.Core.Models;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ActivityProgressTests
{
    [Fact]
    public void Activity_starts_indeterminate_and_switches_to_reported_percentage()
    {
        var vm = new MainWindowViewModel();

        var id = vm.BeginActivity("Downloading Basis");
        vm.ReportActivity(id, "Receiving objects: 47% (100/212)");

        Assert.True(vm.IsActivityVisible);
        Assert.Equal("Downloading Basis", vm.ActivityTitle);
        Assert.Equal("Receiving objects: 47% (100/212)", vm.ActivityDetail);
        Assert.False(vm.ActivityIsIndeterminate);
        Assert.Equal(47, vm.ActivityProgress);

        vm.EndActivity(id);
        Assert.False(vm.IsActivityVisible);
    }

    [Fact]
    public void Older_activity_cannot_overwrite_or_hide_newer_activity()
    {
        var vm = new MainWindowViewModel();
        var oldId = vm.BeginActivity("Old operation");
        var newId = vm.BeginActivity("New operation");

        vm.ReportActivity(oldId, "Receiving objects: 99%");
        vm.EndActivity(oldId);

        Assert.True(vm.IsActivityVisible);
        Assert.Equal("New operation", vm.ActivityTitle);
        Assert.True(vm.ActivityIsIndeterminate);

        vm.EndActivity(newId);
        Assert.False(vm.IsActivityVisible);
    }

    [Fact]
    public void Concurrent_activities_each_keep_their_project_and_progress()
    {
        var vm = new MainWindowViewModel();
        var clone = vm.BeginActivity("Cloning Basis", "Basis Racing");
        var update = vm.BeginActivity("Updating to the latest Basis", "Basis PoiCraft");

        vm.ReportActivity(clone, "Receiving objects: 30%");
        vm.ReportActivity(update, "Merging Basis…");

        Assert.Equal(2, vm.Activities.Count);
        Assert.Equal("Basis PoiCraft", vm.CurrentActivity!.Project);
        Assert.Equal("Merging Basis…", vm.ActivityDetail);
        Assert.True(vm.HasMoreActivities);
        Assert.Equal("Basis Racing", vm.ProjectOf(clone));
        var cloning = vm.Activities.Single(a => a.Id == clone);
        Assert.Equal(30, cloning.Progress);
        Assert.False(cloning.IsIndeterminate);
        Assert.True(vm.IsAnythingRunning);

        vm.EndActivity(update);
        Assert.Same(cloning, vm.CurrentActivity);
        Assert.False(vm.HasMoreActivities);
        Assert.Null(vm.ProjectOf(update));

        vm.EndActivity(clone);
        Assert.False(vm.IsAnythingRunning);
        Assert.Null(vm.CurrentActivity);
    }

    [Fact]
    public void Status_messages_carry_the_project_they_are_about()
    {
        var vm = new MainWindowViewModel();

        vm.SetStatus("Merging Basis…", StatusKind.Info, "Basis PoiCraft");
        Assert.Equal("Merging Basis…", vm.StatusMessage);
        Assert.Equal("Basis PoiCraft", vm.StatusProject);
        Assert.True(vm.HasStatusProject);

        vm.SetStatus("Ready");
        Assert.Null(vm.StatusProject);
        Assert.False(vm.HasStatusProject);
    }

    [AvaloniaFact]
    public void Showing_activities_opens_the_settings_activity_section()
    {
        Localizer.Instance.SetLanguage("en");
        var vm = new MainWindowViewModel();
        var view = new SettingsView { DataContext = vm.SettingsVM };
        _ = new Window { Content = view };
        var section = view.GetLogicalDescendants().OfType<Expander>().Single(e => SectionState.GetKey(e) == "settings.activity");
        section.IsExpanded = false;

        vm.ShowActivities();

        Assert.True(section.IsExpanded);
        Assert.Equal(NavPage.Settings, vm.CurrentPage);
    }

    [Fact]
    public void A_paused_basis_update_is_listed_as_needing_attention()
    {
        var vm = new MainWindowViewModel();
        var row = new InstallRow(new BasisInstall { RepoRoot = Path.Combine(Path.GetTempPath(), "paused"), UnityProjectPath = Path.Combine(Path.GetTempPath(), "paused"), Name = "Paused", Alias = "Basis Paused" });
        row.ApplyBasisCheck(new BasisUpdateCheck(BasisUpdateStatus.UpdateAvailable, "developer", "sha", 1, true));
        vm.InstallsVM.Installs.Add(row);

        vm.RefreshBlockers();

        var blocker = Assert.Single(vm.Blockers, b => b.Project == row.Name);
        Assert.True(blocker.HasAction);
        Assert.True(vm.HasBlockers);
    }
}
