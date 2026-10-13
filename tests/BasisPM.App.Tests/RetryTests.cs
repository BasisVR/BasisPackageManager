using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class RetryTests
{
    private const string Failed = "Couldn't clone com.example.toon: Connection reset";

    private static MainWindowViewModel Shell()
    {
        Localizer.Instance.SetLanguage("en");
        return new MainWindowViewModel();
    }

    private static BlockerItem? FailureCard(MainWindowViewModel shell) => shell.Blockers.FirstOrDefault(b => b.Text == Failed);

    [AvaloniaFact]
    public void A_failure_that_can_be_retried_offers_try_again_and_waits_under_needs_attention()
    {
        var shell = Shell();
        var tries = 0;

        shell.SetStatus(Failed, StatusKind.Error, "My World", () => { tries++; return Task.CompletedTask; }, "package|toon");

        Assert.True(shell.CanRetryStatus);
        var card = FailureCard(shell);
        Assert.NotNull(card);
        Assert.Equal("My World", card.Project);
        Assert.Equal("Try again", card.ActionLabel);
        Assert.True(card.HasDismiss);

        shell.RetryStatusCommand.Execute(null);

        Assert.Equal(1, tries);
        Assert.False(shell.CanRetryStatus);
        Assert.Null(FailureCard(shell));
        Assert.Equal("Trying again…", shell.StatusMessage);
    }

    [AvaloniaFact]
    public void A_newer_message_hides_try_again_but_the_failure_stays_listed()
    {
        var shell = Shell();
        var tries = 0;
        shell.SetStatus(Failed, StatusKind.Error, "My World", () => { tries++; return Task.CompletedTask; }, "package|toon");

        shell.SetStatus("Installing lilToon…", StatusKind.Info, "My World");

        Assert.False(shell.CanRetryStatus);
        var card = FailureCard(shell);
        Assert.NotNull(card);
        card.Action!.Execute(null);
        Assert.Equal(1, tries);
        Assert.Null(FailureCard(shell));
    }

    [AvaloniaFact]
    public void Succeeding_at_the_same_task_clears_its_failure()
    {
        var shell = Shell();
        shell.SetStatus(Failed, StatusKind.Error, "My World", () => Task.CompletedTask, "package|toon");

        shell.SetStatus("Installed CrossTone into My World.", StatusKind.Success, "My World", key: "package|toon");

        Assert.False(shell.CanRetryStatus);
        Assert.Null(FailureCard(shell));
    }

    [AvaloniaFact]
    public void The_same_failure_twice_is_listed_once()
    {
        var shell = Shell();
        shell.SetStatus(Failed, StatusKind.Error, "My World", () => Task.CompletedTask, "package|toon");
        shell.SetStatus(Failed, StatusKind.Error, "My World", () => Task.CompletedTask, "package|toon");

        Assert.Single(shell.Blockers, b => b.Text == Failed);
    }

    [AvaloniaFact]
    public void Dismissing_the_message_or_the_card_drops_the_failure()
    {
        var shell = Shell();
        shell.SetStatus(Failed, StatusKind.Error, "My World", () => Task.CompletedTask, "package|toon");
        shell.DismissStatus();
        Assert.Null(FailureCard(shell));
        Assert.False(shell.CanRetryStatus);

        shell.SetStatus(Failed, StatusKind.Error, "My World", () => Task.CompletedTask, "package|toon");
        FailureCard(shell)!.Dismiss!.Execute(null);
        Assert.Null(FailureCard(shell));
        Assert.False(shell.CanRetryStatus);
    }

    [AvaloniaFact]
    public void An_error_without_a_way_to_retry_offers_nothing()
    {
        var shell = Shell();
        shell.SetStatus(Failed, StatusKind.Error, "My World");
        Assert.False(shell.CanRetryStatus);
        Assert.Null(FailureCard(shell));
    }

    [AvaloniaFact]
    public void Removing_a_package_waits_for_other_work_on_its_project_and_extra_clicks_are_ignored()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", "queue-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "Proj");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1\n");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            const string line = "https://github.com/example/toon.git#v1.2.0";
            var manifest = Path.Combine(project, "Packages", "manifest.json");
            File.WriteAllText(manifest, "{ \"dependencies\": { \"com.example.toon\": \"" + line + "\" } }");

            var shell = Shell();
            var vm = shell.PackagesVM;
            vm.SetActiveInstall(new BasisInstall
            {
                RepoRoot = project,
                UnityProjectPath = project,
                Name = "Proj",
                HasUnityProject = true,
                Manifest = new PackageManifest { Dependencies = { ["com.example.toon"] = line } },
            });
            var row = vm.Available.Single(r => r.Name == "com.example.toon");
            var gate = new TaskCompletionSource();
            var basisUpdate = shell.Work.RunAsync(project, "Updating Proj…", () => gate.Task);

            vm.RemoveCommand.Execute(row.Entry);
            vm.RemoveCommand.Execute(row.Entry);
            for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }

            Assert.Contains("com.example.toon", File.ReadAllText(manifest));
            Assert.Equal("Waiting for this to finish first: Updating Proj…", shell.StatusMessage);
            Assert.True(vm.Available.Single(r => r.Name == "com.example.toon").IsWorking);

            gate.SetResult();
            for (var i = 0; i < 500 && File.ReadAllText(manifest).Contains("com.example.toon"); i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            for (var i = 0; i < 50 && shell.Work.IsBusy(project); i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }

            Assert.True(basisUpdate.IsCompleted);
            Assert.DoesNotContain("com.example.toon", File.ReadAllText(manifest));
            Assert.Equal("Removed com.example.toon.", shell.StatusMessage);
            Assert.False(shell.Work.IsBusy(project));
        }
        finally
        {
            try { BasisInstallService.DeleteFolderAsync(root).GetAwaiter().GetResult(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
