using System.Diagnostics;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class BasisDevViewModelTests
{
    private static void Git(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo(new GitService().FindGit()!) { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "-c", "core.autocrlf=false" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    [AvaloniaFact]
    public void Clone_that_unity_does_not_load_shows_up_for_reconciling_and_is_not_marked_mounted()
    {
        if (!new GitService().IsAvailable) return;
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", "basisdev-vm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = Path.Combine(root, "Proj");
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.0f1\n");
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{ \"dependencies\": {} }");
            var clone = Path.Combine(project, ".basisdev", "com.example.idle");
            Directory.CreateDirectory(Path.Combine(clone, "Packages", "com.example.idle"));
            File.WriteAllText(Path.Combine(clone, "Packages", "com.example.idle", "package.json"), "{ \"name\": \"com.example.idle\", \"version\": \"1.0.0\" }");
            Git(clone, "init", "-q", "-b", "main");
            Git(clone, "add", "-A");
            Git(clone, "commit", "-q", "-m", "package");
            Git(clone, "remote", "add", "origin", "https://github.com/example/idle.git");

            var vm = new MainWindowViewModel().PackagesVM;
            vm.SetActiveInstall(new BasisInstall { RepoRoot = project, UnityProjectPath = project, Name = "Proj", HasUnityProject = true });
            for (var i = 0; i < 500 && !vm.HasBasisDevIssues; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            var item = Assert.Single(vm.BasisDevIssues);
            Assert.Equal("com.example.idle", item.Id);
            Assert.Equal("Clone not in use", item.IssueLabel);
            Assert.Contains(item.Choices, c => c.Action == BasisDevAction.Record);
            Assert.Contains(item.Choices, c => c.Action == BasisDevAction.Release);
            Assert.True(vm.CanFixBasisDevRecords);
            Assert.DoesNotContain(vm.Available, r => r.Name == "com.example.idle" && r.IsMounted);
        }
        finally
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
