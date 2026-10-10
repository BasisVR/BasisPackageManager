using System.Diagnostics;
using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class ProjectPartsTests
{
    private static BasisPartState State(BasisPart part, bool included, string[]? unsaved = null, int ignored = 0, string[]? repositories = null) =>
        new(part, BasisPartsService.PathOf(part, "Basis"), included, unsaved ?? Array.Empty<string>(), ignored, repositories ?? Array.Empty<string>());

    [AvaloniaFact]
    public void Clone_dialog_starts_from_the_remembered_choice()
    {
        Localizer.Instance.SetLanguage("en");
        var model = new CloneBasisViewModel(@"D:\Worlds\Basis", new[] { "server", "nonsense" });

        Assert.Equal(new[] { "Basis Server", "Images" }, model.Parts.Select(p => p.Name));
        Assert.False(model.Parts[0].IsIncluded);
        Assert.True(model.Parts[1].IsIncluded);
        Assert.Equal("Basis/Images", model.Parts[1].Location);
        Assert.Equal(new[] { BasisPartsService.Server }, model.LeaveOut);

        model.Parts[0].IsIncluded = true;
        model.Parts[1].IsIncluded = false;
        Assert.Equal(new[] { BasisPartsService.Images }, model.LeaveOut);
    }

    [AvaloniaFact]
    public async Task Parts_dialog_blocks_leaving_out_unsaved_work_and_offers_to_delete_ignored_files()
    {
        Localizer.Instance.SetLanguage("en");
        var report = new BasisPartsReport(BasisPartsMode.Managed, @"C:\Basis", new[]
        {
            State(BasisPartsService.Server, true, ignored: 2582, repositories: new[] { "Basis Server/Packages/com.example.server" }),
            State(BasisPartsService.Images, true, unsaved: new[] { "Basis/Images/Mine.png" }),
        });
        var model = new ProjectPartsViewModel("Basis", () => Task.FromResult(report));
        await model.LoadAsync();

        var server = model.Parts[0];
        var images = model.Parts[1];
        Assert.False(model.IsLoading);
        Assert.True(model.HasParts);
        Assert.False(model.CanApply);
        Assert.True(server.CanToggle);
        Assert.False(images.CanToggle);
        Assert.Equal("Not committed yet: Basis/Images/Mine.png. Commit, move or discard these changes before leaving this out.", images.UnsavedText);
        Assert.False(model.ShowDeleteIgnored);

        server.IsIncluded = false;
        Assert.True(model.CanApply);
        Assert.True(model.ShowDeleteIgnored);
        Assert.Equal(2582, model.IgnoredToDelete);
        Assert.True(server.ShowsRepositories);
        Assert.Equal("Git clones inside it stay where they are: Basis Server/Packages/com.example.server.", server.RepositoriesText);

        model.DeleteIgnored = false;
        Assert.Equal(new[] { BasisPartsService.Server }, model.Choice.LeaveOut);
        Assert.False(model.Choice.DeleteIgnored);
    }

    [AvaloniaFact]
    public async Task Parts_dialog_leaves_a_hand_made_sparse_checkout_alone()
    {
        Localizer.Instance.SetLanguage("en");
        var report = new BasisPartsReport(BasisPartsMode.Custom, @"C:\Basis", new[] { State(BasisPartsService.Server, false) });
        var model = new ProjectPartsViewModel("Basis", () => Task.FromResult(report));
        await model.LoadAsync();

        Assert.True(model.IsCustom);
        Assert.False(model.Parts[0].CanToggle);
        model.Parts[0].IsIncluded = true;
        Assert.False(model.CanApply);
    }

    [AvaloniaFact]
    public void Results_read_as_one_status_line()
    {
        Localizer.Instance.SetLanguage("en");
        var removed = BasisPartsResult.Unchanged with { Removed = new[] { BasisPartsService.Server }, DeletedFiles = 1200, Kept = new[] { "Basis Server/Packages/com.example.server" } };
        Assert.Equal("Left Basis Server out of Basis. Removed the files git ignores there (1,200 in all). Some files stayed: Basis Server/Packages/com.example.server.",
            PartsText.Describe("Basis", removed));
        Assert.Equal("Added Basis Server, Images back to Basis.", PartsText.Describe("Basis", BasisPartsResult.Unchanged with { Added = new[] { BasisPartsService.Server, BasisPartsService.Images } }));
        Assert.Equal("These files have changes that aren't committed yet: a, b, c and 2 more. Commit, move or discard them first.",
            PartsText.Describe("Basis", BasisPartsResult.Fail(BasisPartsFailure.UnsavedWork, "a\nb\nc\nd\ne")));
        Assert.Equal("Nothing changed in Basis.", PartsText.Describe("Basis", BasisPartsResult.Unchanged));
    }

    [AvaloniaFact]
    public async Task Leaving_the_server_out_shows_on_the_card_and_the_server_tab_brings_it_back()
    {
        Localizer.Instance.SetLanguage("en");
        if (!new GitService().IsAvailable) return;
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"parts-{Guid.NewGuid():N}");
        try
        {
            var project = Path.Combine(root, "Basis");
            Directory.CreateDirectory(project);
            Git(project, "init", "-q", "-b", "developer");
            Write(project, ".gitattributes", "* -text\n");
            Write(project, "Basis Server/.gitignore", "bin/\n");
            Write(project, "Basis Server/BasisServerConsole/Program.cs", "server\n");
            Write(project, "Basis/Assets/Scene.unity", "scene\n");
            Write(project, "Basis/Packages/com.basis.framework/package.json", "{}\n");
            Git(project, "add", "-A");
            Git(project, "commit", "-q", "-m", "basis");
            Write(project, "Basis Server/BasisServerConsole/bin/server.dll", "built\n");

            var shell = new MainWindowViewModel();
            var git = new GitService();
            var vm = new InstallsViewModel(new UserSettingsService(Path.Combine(root, "settings.json")), new BasisInstallService(new UnityProjectService(), git), git, new BasisUpdateService(git), shell);
            var row = new InstallRow(new BasisInstall { RepoRoot = project, UnityProjectPath = Path.Combine(project, "Basis"), Name = "Basis", IsGitRepo = true, HasUnityProject = true });
            vm.Installs.Add(row);

            var result = await vm.SetPartsAsync(row, new[] { BasisPartsService.Server }, deleteIgnored: true);

            Assert.True(result is { Ok: true }, result?.Detail);
            Assert.False(Directory.Exists(Path.Combine(project, "Basis Server")));
            Assert.Equal(new[] { BasisPartsService.Server }, row.LeftOutParts);
            Assert.Equal("Without Basis Server", row.LeftOutText);
            Assert.Equal("Left Basis Server out of Basis. Removed the files git ignores there (1 in all).", shell.StatusMessage);

            await vm.IncludePartAsync(row.Install, BasisPartsService.Server);

            Assert.Equal("server\n", File.ReadAllText(Path.Combine(project, "Basis Server", "BasisServerConsole", "Program.cs")));
            Assert.False(row.HasLeftOutParts);
            Assert.Equal("Added Basis Server back to Basis.", shell.StatusMessage);
            Assert.Equal("", Git(project, "status", "--porcelain"));
        }
        finally
        {
            try { await BasisInstallService.DeleteFolderAsync(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Write(string repo, string relativePath, string content)
    {
        var full = Path.Combine(repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
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
        foreach (var a in new[] { "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "-c", "core.autocrlf=false" })
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
