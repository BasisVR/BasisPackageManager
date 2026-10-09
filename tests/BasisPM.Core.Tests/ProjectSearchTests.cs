using System.Diagnostics;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ProjectSearchTests
{
    private readonly BasisInstallService _svc = new(new UnityProjectService(), new GitService());

    [Fact]
    public void Finds_basis_projects_and_keeps_each_where_it_loads_from()
    {
        using var t = new TempDir("search");
        t.CreateDir("Github/Basis/.git");
        var basis = BasisProject(t, "Github/Basis/Basis");
        t.CreateDir("Github/Game/.git");
        var game = BasisProject(t, "Github/Game", "6000.2.0f1");
        var loose = BasisProject(t, "Github/Worlds/Silk/Basis Silk");
        t.CreateDir("Github/Plain/Assets");
        t.WriteFile("Github/Plain/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 2022.3.0f1\n");
        var searched = 0;

        var found = _svc.FindProjects(t.Combine("Github"), count => searched = count).ToList();

        Assert.Equal(3, found.Count);
        Assert.Contains(new FoundProject(t.Combine("Github/Basis"), basis, "6000.0.25f1", "Basis"), found);
        Assert.Contains(new FoundProject(game, game, "6000.2.0f1", "Basis Game"), found);
        Assert.Contains(new FoundProject(loose, loose, "6000.0.25f1", "Basis Silk"), found);
        Assert.True(searched >= 6);
    }

    [Fact]
    public void Names_each_project_basis_and_its_folder()
    {
        using var t = new TempDir("search");
        t.CreateDir("Github/Basis/.git");
        BasisProject(t, "Github/Basis/Basis");
        t.CreateDir("Github/PoiCraft/.git");
        BasisProject(t, "Github/PoiCraft/Basis");
        t.CreateDir("Github/Basis Load Test/Basis/.git");
        BasisProject(t, "Github/Basis Load Test/Basis/Basis");
        BasisProject(t, "Github/MinexBasis/Basis");
        BasisProject(t, "Github/Basis Tube");

        var names = _svc.FindProjects(t.Combine("Github")).ToDictionary(p => p.RepoRoot, p => p.Name);

        Assert.Equal("Basis", names[t.Combine("Github/Basis")]);
        Assert.Equal("Basis PoiCraft", names[t.Combine("Github/PoiCraft")]);
        Assert.Equal("Basis Load Test", names[t.Combine("Github/Basis Load Test/Basis")]);
        Assert.Equal("MinexBasis", names[t.Combine("Github/MinexBasis/Basis")]);
        Assert.Equal("Basis Tube", names[t.Combine("Github/Basis Tube")]);
        Assert.Equal("Basis PoiCraft", Assert.Single(_svc.FindProjects(t.Combine("Github/PoiCraft"))).Name);
    }

    [Fact]
    public void Two_projects_in_one_repository_each_load_as_themselves()
    {
        using var t = new TempDir("search");
        t.CreateDir("mono/.git");
        BasisProject(t, "mono/Alpha");
        BasisProject(t, "mono/Beta");

        var found = _svc.FindProjects(t.Combine("mono")).ToList();

        Assert.Equal(2, found.Select(p => p.RepoRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var project in found)
            Assert.Equal(project.UnityProjectPath, new UnityProjectService().Detect(project.RepoRoot).ResolvedPath);
    }

    [Fact]
    public void Skips_hidden_and_build_folders_and_stops_at_the_depth_limit()
    {
        using var t = new TempDir("search");
        BasisProject(t, "root/.basisdev/clone");
        BasisProject(t, "root/tools/node_modules/pkg");
        var chain = string.Join("/", Enumerable.Range(1, BasisInstallService.ProjectSearchDepth - 1).Select(i => $"n{i}"));
        BasisProject(t, $"root/{chain}/past/deep");
        var shallow = BasisProject(t, $"root/{chain}/ok");

        var found = _svc.FindProjects(t.Combine("root")).ToList();

        Assert.Equal(shallow, Assert.Single(found).UnityProjectPath);
    }

    [Fact]
    public void Looks_two_levels_into_a_repository_unless_the_search_starts_in_it()
    {
        using var t = new TempDir("search");
        t.CreateDir("root/mono/.git");
        var near = BasisProject(t, "root/mono/games/Near");
        BasisProject(t, "root/mono/clients/acme/Far");

        Assert.Equal(new[] { near }, _svc.FindProjects(t.Combine("root")).Select(p => p.UnityProjectPath));
        Assert.Equal(2, _svc.FindProjects(t.Combine("root/mono")).Count());
    }

    [Fact]
    public void Does_not_follow_a_link_back_into_the_searched_folder()
    {
        using var t = new TempDir("search");
        var project = BasisProject(t, "root/projects/Basis");
        if (!TryLink(t.Combine("root/projects/loop"), t.Combine("root"))) return;

        var found = _svc.FindProjects(t.Combine("root")).ToList();

        Assert.Equal(project, Assert.Single(found).UnityProjectPath);
    }

    [Fact]
    public void Stops_when_cancelled()
    {
        using var t = new TempDir("search");
        BasisProject(t, "root/Basis");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => _svc.FindProjects(t.Combine("root"), null, cancel.Token).ToList());
    }

    private static string BasisProject(TempDir t, string path, string version = "6000.0.25f1")
    {
        t.CreateDir(path + "/Assets");
        t.WriteFile(path + "/ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {version}\n");
        t.WriteFile(path + "/Packages/com.basis.framework/package.json", "{}");
        return t.Combine(path);
    }

    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!OperatingSystem.IsWindows()) return false;
        using var junction = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        junction?.WaitForExit();
        return Directory.Exists(link);
    }
}
