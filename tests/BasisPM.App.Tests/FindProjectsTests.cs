using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class FindProjectsTests
{
    [AvaloniaFact]
    public async Task Search_lists_projects_and_marks_the_ones_already_in_the_list()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            var known = BasisProject(root, "Known");
            var fresh = BasisProject(root, "Projects/Fresh");
            var installs = new BasisInstallService(new UnityProjectService(), new GitService());
            var search = new FindProjectsViewModel(installs, new[] { await installs.LoadAsync(known) });

            await search.SearchAsync(root);
            Dispatcher.UIThread.RunJobs();

            Assert.False(search.IsSearching);
            Assert.Equal(2, search.Results.Count);
            Assert.StartsWith("Search finished. Found: 2.", search.StatusText);
            var knownRow = search.Results.Single(r => r.Location == known);
            Assert.True(knownRow.AlreadyAdded);
            Assert.False(knownRow.IsSelected);
            knownRow.IsSelected = true;
            Assert.False(knownRow.IsSelected);
            var freshRow = search.Results.Single(r => r.Location == fresh);
            Assert.True(freshRow.IsSelected);
            Assert.Equal("Basis Fresh", freshRow.Name);
            Assert.Equal("Unity 6000.0.25f1", freshRow.UnityLabel);
            Assert.Equal(new[] { fresh }, search.Selected.Select(p => p.UnityProjectPath));

            freshRow.IsSelected = false;
            Assert.False(search.CanAdd);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public async Task Search_says_when_nothing_was_found()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Empty", "Nested"));
            var search = new FindProjectsViewModel(new BasisInstallService(new UnityProjectService(), new GitService()), Array.Empty<BasisInstall>());

            await search.SearchAsync(root);
            Dispatcher.UIThread.RunJobs();

            Assert.False(search.HasResults);
            Assert.True(search.ShowNothingFound);
            Assert.Equal("Search finished. Found: 0. Folders checked: 3.", search.StatusText);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public async Task Adding_found_projects_puts_them_in_the_list_once_and_saves_them()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            var one = BasisProject(root, "One");
            var two = BasisProject(root, "Two");
            var service = new UserSettingsService(Path.Combine(root, "settings.json"));
            var git = new GitService();
            var installs = new BasisInstallService(new UnityProjectService(), git);
            var vm = new InstallsViewModel(service, installs, git, new BasisUpdateService(git), new MainWindowViewModel());
            await vm.LoadAsync(new UserSettings());
            var found = installs.FindProjects(root).ToList();

            Assert.Equal(2, await vm.AddFoundAsync(found));
            Assert.Equal(0, await vm.AddFoundAsync(found));

            Assert.Equal(new[] { one, two }, vm.Installs.Select(r => r.RepoRoot).Order(StringComparer.Ordinal));
            Assert.Equal(new[] { "Basis One", "Basis Two" }, vm.Installs.Select(r => r.Install.DisplayName).Order(StringComparer.Ordinal));
            var saved = await service.LoadAsync();
            Assert.Equal(new[] { one, two }, saved.Installs.Order(StringComparer.Ordinal));
            Assert.Equal("Basis One", saved.InstallAliases[one]);
        }
        finally { Delete(root); }
    }

    private static string BasisProject(string root, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(root, relative));
        Directory.CreateDirectory(Path.Combine(path, "Assets"));
        Directory.CreateDirectory(Path.Combine(path, "ProjectSettings"));
        Directory.CreateDirectory(Path.Combine(path, "Packages", "com.basis.framework"));
        File.WriteAllText(Path.Combine(path, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 6000.0.25f1\n");
        File.WriteAllText(Path.Combine(path, "Packages", "com.basis.framework", "package.json"), "{}");
        return path;
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"find-projects-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string root)
    {
        try { Directory.Delete(root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
