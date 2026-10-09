using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class BuiltInPackagesTests
{
    private const string AddonUrl = "https://github.com/someone/addon.git";
    private const string VendorUrl = "https://github.com/vendor/utils.git#abc123";

    [AvaloniaFact]
    public async Task Embedded_packages_show_only_under_built_in()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            Write(root, "Packages/com.basis.framework/package.json", """{ "name": "com.basis.framework", "displayName": "Basis Framework", "version": "1.2.0" }""");
            var vm = new MainWindowViewModel().PackagesVM;
            vm.SetActiveInstall(Install(root, ("com.community.addon", AddonUrl), ("com.unity.timeline", "1.0.0")));

            Assert.Equal(new[] { "com.community.addon" }, vm.Available.Select(r => r.Name));
            Assert.Equal(1, vm.SourceFacets.Single(f => f.Key == "all").Count);
            var builtIn = vm.SourceFacets.Single(f => f.Key == "built-in");
            Assert.Equal(1, builtIn.Count);
            Assert.Equal("Built-in", builtIn.Label);

            vm.SelectedSourceFacet = builtIn;
            var row = Assert.Single(vm.Available);
            Assert.Equal("com.basis.framework", row.Name);
            Assert.Equal("Basis Framework", row.DisplayName);
            Assert.Equal("1.2.0", row.InstalledLabel);
            Assert.True(row.IsIncluded);
            Assert.False(row.CanRemove);

            vm.SelectedSourceFacet = vm.SourceFacets.Single(f => f.Key == "all");
            Assert.Equal(new[] { "com.community.addon" }, vm.Available.Select(r => r.Name));
        }
        finally { await DeleteAsync(root); }
    }

    [AvaloniaFact]
    public async Task Dependencies_basis_declares_move_to_built_in()
    {
        var git = new GitService();
        if (!git.IsAvailable) return;
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            Write(root, "Packages/manifest.json", $$"""{ "dependencies": { "com.vendor.utils": "{{VendorUrl}}" } }""");
            Assert.True((await git.InitAsync(root)).Ok);
            Assert.True((await git.AddAllAsync(root)).Ok);
            Assert.True((await git.CommitAsync(root, "basis", "Tester", "tester@example.com")).Ok);
            var vm = new MainWindowViewModel().PackagesVM;
            vm.SetActiveInstall(Install(root, ("com.vendor.utils", VendorUrl), ("com.community.addon", AddonUrl)));

            for (var i = 0; i < 400 && vm.Available.Any(r => r.Name == "com.vendor.utils"); i++) await Task.Delay(25);

            Assert.Equal(new[] { "com.community.addon" }, vm.Available.Select(r => r.Name));
            vm.SelectedSourceFacet = vm.SourceFacets.Single(f => f.Key == "built-in");
            var row = Assert.Single(vm.Available);
            Assert.Equal("com.vendor.utils", row.Name);
            Assert.True(row.IsIncluded);
            Assert.False(row.IsEmbedded);
            Assert.False(row.CanUpdate);
            Assert.False(row.CanMountToEdit);
            Assert.True(row.HasGitUrl);
        }
        finally { await DeleteAsync(root); }
    }

    [AvaloniaFact]
    public async Task A_projects_own_embedded_package_stays_under_all()
    {
        var git = new GitService();
        if (!git.IsAvailable) return;
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            Write(root, "Packages/manifest.json", """{ "dependencies": {} }""");
            Write(root, "Packages/com.basis.framework/package.json", """{ "name": "com.basis.framework", "displayName": "Basis Framework", "version": "1.2.0" }""");
            Assert.True((await git.InitAsync(root)).Ok);
            Assert.True((await git.AddAllAsync(root)).Ok);
            Assert.True((await git.CommitAsync(root, "basis", "Tester", "tester@example.com")).Ok);
            Write(root, "Packages/com.studio.tools/package.json", """{ "name": "com.studio.tools", "displayName": "Studio Tools", "version": "0.1.0" }""");
            var vm = new MainWindowViewModel().PackagesVM;
            vm.SetActiveInstall(Install(root));

            for (var i = 0; i < 400 && !vm.Available.Any(r => r.Name == "com.studio.tools"); i++) await Task.Delay(25);

            var own = Assert.Single(vm.Available);
            Assert.Equal("com.studio.tools", own.Name);
            Assert.True(own.IsEmbedded);
            Assert.False(own.IsIncluded);
            Assert.False(own.CanUpdate);
            Assert.False(own.CanRemove);
            vm.SelectedSourceFacet = vm.SourceFacets.Single(f => f.Key == "built-in");
            var shipped = Assert.Single(vm.Available);
            Assert.Equal("com.basis.framework", shipped.Name);
            Assert.True(shipped.IsIncluded);
        }
        finally { await DeleteAsync(root); }
    }

    private static async Task DeleteAsync(string root)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await BasisInstallService.DeleteFolderAsync(root); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20) { await Task.Delay(50); }
        }
    }

    [AvaloniaFact]
    public async Task A_mount_shadowed_by_the_basis_copy_counts_as_built_in()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        var data = root + "-data";
        try
        {
            Write(root, "Packages/dev.vendor.tool/package.json", """{ "name": "dev.vendor.tool", "displayName": "Vendor Tool", "version": "1.0.0" }""");
            Directory.CreateDirectory(Path.Combine(root, ".basisdev", "dev.vendor.tool", ".git"));
            Write(root, "Packages/com.community.cloned/package.json", """{ "name": "com.community.cloned", "version": "2.0.0" }""");
            Directory.CreateDirectory(Path.Combine(root, "Packages", "com.community.cloned", ".git"));
            Directory.CreateDirectory(data);
            var registry = new MountRegistry(data);
            registry.Add(new MountRecord(root, "dev.vendor.tool", Path.Combine(root, ".basisdev", "dev.vendor.tool"), "https://github.com/vendor/tool.git?path=Packages/dev.vendor.tool#main"));
            registry.Add(new MountRecord(root, "com.community.cloned", Path.Combine(root, "Packages", "com.community.cloned"), "https://github.com/someone/cloned.git#v2"));
            var vm = Standalone(registry);
            vm.SetActiveInstall(Install(root));

            var cloned = Assert.Single(vm.Available);
            Assert.Equal("com.community.cloned", cloned.Name);
            Assert.True(cloned.IsMounted);

            vm.SelectedSourceFacet = vm.SourceFacets.Single(f => f.Key == "built-in");
            var shadowed = Assert.Single(vm.Available);
            Assert.Equal("dev.vendor.tool", shadowed.Name);
            Assert.True(shadowed.IsIncluded);
            Assert.False(shadowed.CanUpdate);
            Assert.False(shadowed.CanRemove);
            Assert.False(shadowed.CanChooseVersion);
            Assert.False(cloned.IsIncluded);
            Assert.True(cloned.CanRemove);
        }
        finally
        {
            await DeleteAsync(root);
            await DeleteAsync(data);
        }
    }

    private static PackagesViewModel Standalone(MountRegistry registry)
    {
        var git = new GitService();
        var projects = new UnityProjectService();
        var api = new GitHubApiService();
        return new PackagesViewModel(new UserSettingsService(), new CatalogService(), projects, registry, new MountService(git, projects, registry),
            new ContributeService(git, api), new CacheDriftService(git), new GitHubAuthService(), api, git, new BasisUpdateService(git), new MainWindowViewModel());
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", $"builtin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        return root;
    }

    private static BasisInstall Install(string root, params (string Id, string Value)[] dependencies)
    {
        var manifest = new PackageManifest();
        foreach (var (id, value) in dependencies) manifest.Dependencies[id] = value;
        return new BasisInstall { RepoRoot = root, UnityProjectPath = root, Name = "Game", HasUnityProject = true, Manifest = manifest };
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
