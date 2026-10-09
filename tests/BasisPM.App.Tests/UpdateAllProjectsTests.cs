using System.Diagnostics;
using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class UpdateAllProjectsTests
{
    [AvaloniaFact]
    public async Task Update_all_brings_clean_projects_up_to_date_and_leaves_conflicting_ones_alone()
    {
        Localizer.Instance.SetLanguage("en");
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"update-all-{Guid.NewGuid():N}");
        try
        {
            var upstream = Path.Combine(root, "upstream");
            Directory.CreateDirectory(upstream);
            Git(upstream, "init", "-q", "-b", "developer");
            Git(upstream, "config", "core.autocrlf", "false");
            Commit(upstream, "base", ("Basis/a.txt", "one\n"), ("Basis/b.txt", "b\n"));
            var clean = Clone(root, upstream, "Clean");
            var edited = Clone(root, upstream, "Edited");
            var editedHead = Commit(edited, "my tweak", ("Basis/a.txt", "mine\n"));
            var tip = Commit(upstream, "basis change", ("Basis/a.txt", "two\n"));
            var current = Clone(root, upstream, "Current");

            var shell = new MainWindowViewModel();
            var git = new GitService(allowLocalRemotes: true);
            var vm = new InstallsViewModel(new UserSettingsService(Path.Combine(root, "settings.json")),
                new BasisInstallService(new UnityProjectService(), git), git, new BasisUpdateService(git, upstream), shell);
            foreach (var (name, path) in new[] { ("Clean", clean), ("Edited", edited), ("Current", current) }) vm.Installs.Add(Row(name, path));

            string? asked = null;
            await vm.UpdateAllAsync((_, message) => { asked = message; return Task.FromResult(true); });

            Assert.Equal(tip, Git(clean, "rev-parse", "HEAD"));
            Assert.Equal(editedHead, Git(edited, "rev-parse", "HEAD"));
            Assert.Equal("mine\n", File.ReadAllText(Path.Combine(edited, "Basis", "a.txt")));
            Assert.Equal("", Git(edited, "status", "--porcelain"));
            Assert.Contains("Clean: 1 new commits on developer", asked);
            Assert.Contains("Edited: would conflict with your changes", asked);
            Assert.DoesNotContain("Current", asked);
            Assert.Equal("Updated to the latest Basis: Clean. Needs you: Edited (would conflict with your changes)", shell.StatusMessage);

            asked = null;
            await vm.UpdateAllAsync((_, message) => { asked = message; return Task.FromResult(true); });
            Assert.Null(asked);
            Assert.Equal("Nothing was updated. Needs you: Edited (would conflict with your changes)", shell.StatusMessage);
        }
        finally
        {
            try { await BasisInstallService.DeleteFolderAsync(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static InstallRow Row(string name, string path) => new(new BasisInstall
    {
        RepoRoot = path,
        UnityProjectPath = Path.Combine(path, "Basis"),
        Name = name,
        IsGitRepo = true,
        IsBasisCheckout = true,
    });

    private static string Clone(string root, string upstream, string name)
    {
        var path = Path.Combine(root, name);
        Git(root, "clone", "-q", "-b", "developer", upstream, path);
        Git(path, "config", "core.autocrlf", "false");
        return path;
    }

    private static string Commit(string repo, string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(repo, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        Git(repo, "add", "-A");
        Git(repo, "commit", "-q", "-m", message);
        return Git(repo, "rev-parse", "HEAD");
    }

    private static string Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(new GitService().FindGit() ?? throw new InvalidOperationException("git not found"))
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-c", "protocol.file.allow=always", "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "-c", "core.autocrlf=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr.Result}{stdout.Result}");
        return stdout.Result.TrimEnd();
    }
}
