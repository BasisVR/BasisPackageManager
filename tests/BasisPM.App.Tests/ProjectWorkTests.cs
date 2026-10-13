using BasisPM.App.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectWorkTests
{
    private static readonly string Project = Path.Combine(Path.GetTempPath(), "basispm-tests", "work-project");
    private static readonly string OtherProject = Path.Combine(Path.GetTempPath(), "basispm-tests", "work-other");

    [Fact]
    public async Task Work_on_one_project_runs_one_task_at_a_time_in_the_order_it_was_asked_for()
    {
        var work = new ProjectWork();
        var gate = new TaskCompletionSource();
        var order = new List<string>();
        string? waitedFor = null;

        var first = work.RunAsync(Project, "Installing lilToon", async () => { order.Add("install started"); await gate.Task; order.Add("install done"); });
        var second = work.RunAsync(Project, "Removing CrossTone", () => { order.Add("remove"); return Task.CompletedTask; }, running => waitedFor = running);
        var third = work.RunAsync(Project + Path.DirectorySeparatorChar, "Updating Basis", () => { order.Add("update"); return Task.CompletedTask; });

        Assert.True(work.IsBusy(Project));
        Assert.Equal("Installing lilToon", work.Running(Project));
        Assert.Equal("Installing lilToon", waitedFor);
        Assert.Equal(new[] { "install started" }, order);

        gate.SetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(new[] { "install started", "install done", "remove", "update" }, order);
        Assert.False(work.IsBusy(Project));
        Assert.Null(work.Running(Project));
    }

    [Fact]
    public async Task Different_projects_do_not_wait_for_each_other()
    {
        var work = new ProjectWork();
        var gate = new TaskCompletionSource();
        var busy = work.RunAsync(Project, "Updating Basis", () => gate.Task);

        var ran = false;
        await work.RunAsync(OtherProject, "Installing lilToon", () => { ran = true; return Task.CompletedTask; });

        Assert.True(ran);
        Assert.True(work.IsBusy(Project));
        gate.SetResult();
        await busy;
    }

    [Fact]
    public async Task A_task_already_running_for_a_project_can_call_into_more_work_for_it()
    {
        var work = new ProjectWork();
        var inner = false;

        var outer = work.RunAsync(Project, "Updating Basis", () => work.RunAsync(Project, "Backing up", () => { inner = true; return Task.CompletedTask; }));

        Assert.Same(outer, await Task.WhenAny(outer, Task.Delay(5000)));
        Assert.True(inner);
        Assert.False(work.IsBusy(Project));
    }

    [Fact]
    public async Task Work_started_from_inside_a_finished_task_still_waits_for_its_turn()
    {
        var work = new ProjectWork();
        var release = new TaskCompletionSource();
        Task? late = null;
        await work.RunAsync(Project, "Installing lilToon", () =>
        {
            late = Task.Run(async () =>
            {
                await release.Task;
                await work.RunAsync(Project, "Late", () => Task.CompletedTask);
            });
            return Task.CompletedTask;
        });

        var gate = new TaskCompletionSource();
        var holder = work.RunAsync(Project, "Removing CrossTone", () => gate.Task);
        release.SetResult();
        await Task.Delay(200);

        Assert.False(late!.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(holder, late);
    }

    [Fact]
    public async Task A_failed_task_lets_the_next_one_run()
    {
        var work = new ProjectWork();
        var gate = new TaskCompletionSource();
        var failing = work.RunAsync(Project, "Installing lilToon", async () => { await gate.Task; throw new InvalidOperationException("clone failed"); });
        var ran = false;
        var next = work.RunAsync(Project, "Removing CrossTone", () => { ran = true; return Task.CompletedTask; });

        gate.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        await next;

        Assert.True(ran);
        Assert.False(work.IsBusy(Project));
    }

    [Fact]
    public async Task Work_returns_its_result()
    {
        var work = new ProjectWork();
        Assert.Equal(42, await work.RunAsync(Project, "Counting", () => Task.FromResult(42)));
    }
}
