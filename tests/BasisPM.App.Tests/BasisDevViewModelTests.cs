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
    public void A_listed_package_that_is_not_here_yet_is_a_download_not_a_card()
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
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{ \"dependencies\": { \"com.example.team\": \"file:../.basisdev/com.example.team\" } }");
            BasisPackagesStore.Set(project, "com.example.team", new BasisPackageEntry { Url = "https://github.com/example/team.git", Commit = new string('a', 40) });

            var vm = new MainWindowViewModel().PackagesVM;
            vm.SetActiveInstall(new BasisInstall { RepoRoot = project, UnityProjectPath = project, Name = "Proj", HasUnityProject = true });
            for (var i = 0; i < 500 && !vm.HasBasisDevIssues; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(10);
            }

            Assert.Empty(vm.BasisDevIssues);
            Assert.True(vm.HasBasisDevDownloads);
            Assert.Equal("1 package(s) to download", vm.BasisDevTitle);
            Assert.Contains("com.example.team", vm.BasisDevDownloadText);
            Assert.Equal("Download (1)", vm.RestoreAllLabel);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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
            Assert.DoesNotContain(item.Choices, c => c.Action == BasisDevAction.Record);
            Assert.Contains(item.Choices, c => c.Action == BasisDevAction.Release);
            Assert.False(vm.CanFixBasisDevRecords);
            Assert.True(File.Exists(Path.Combine(project, ".basisdev", "com.example.idle.json")));
            Assert.Equal("1 package(s) need a decision", vm.BasisDevTitle);
            Assert.False(vm.HasBasisDevDownloads);
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
