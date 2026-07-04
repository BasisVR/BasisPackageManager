using BasisPM.App.ViewModels;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class RelayCommandTests
{
    [Fact]
    public void CanExecute_is_true_by_default()
    {
        Assert.True(new RelayCommand(() => Task.CompletedTask).CanExecute(null));
    }

    [Fact]
    public void CanExecute_respects_the_predicate()
    {
        Assert.False(new RelayCommand(() => { }, canExec: () => false).CanExecute(null));
        Assert.True(new RelayCommand(() => { }, canExec: () => true).CanExecute(null));
    }

    [Fact]
    public void Execute_runs_a_synchronous_action()
    {
        var ran = false;
        new RelayCommand(() => { ran = true; }).Execute(null);
        Assert.True(ran);
    }

    [Fact]
    public void Execute_runs_a_completed_task_delegate()
    {
        var ran = false;
        new RelayCommand(() => { ran = true; return Task.CompletedTask; }).Execute(null);
        Assert.True(ran);
    }

    [Fact]
    public void Execute_does_nothing_when_it_cannot_execute()
    {
        var ran = false;
        new RelayCommand(() => { ran = true; }, canExec: () => false).Execute(null);
        Assert.False(ran);
    }

    [Fact]
    public void Execute_raises_can_execute_changed_at_start_and_end()
    {
        var count = 0;
        var cmd = new RelayCommand(() => { });
        cmd.CanExecuteChanged += (_, _) => count++;

        cmd.Execute(null);

        Assert.Equal(2, count);
    }

    [Fact]
    public void Raise_fires_can_execute_changed()
    {
        var count = 0;
        var cmd = new RelayCommand(() => { });
        cmd.CanExecuteChanged += (_, _) => count++;
        cmd.Raise();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Generic_passes_the_typed_parameter()
    {
        string? got = null;
        new RelayCommand<string>(p => { got = p; }).Execute("hello");
        Assert.Equal("hello", got);
    }

    [Fact]
    public void Generic_casts_an_incompatible_parameter_to_default()
    {
        object? got = "initial";
        new RelayCommand<string>(p => { got = p; }).Execute(123);
        Assert.Null(got);
    }

    [Fact]
    public void Generic_can_execute_predicate_receives_typed_value()
    {
        var cmd = new RelayCommand<string>(_ => { }, canExec: p => p == "ok");
        Assert.True(cmd.CanExecute("ok"));
        Assert.False(cmd.CanExecute("no"));
    }
}
