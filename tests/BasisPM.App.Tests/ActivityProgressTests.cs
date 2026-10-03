using BasisPM.App.ViewModels;
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
}
