using Avalonia.Headless.XUnit;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class StorageAndLicensesTests
{
    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "basispm-tests", "storage-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Write(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Delete(string root)
    {
        try { FileRetry.DeleteDirectory(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static BasisInstall Project(string root)
    {
        Write(root, "Project/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.25f1\n");
        Write(root, "Project/Assets/Scene.unity", new string('a', 100));
        Write(root, "Project/Packages/manifest.json", "{ \"dependencies\": { } }");
        Write(root, "Project/Packages/com.test.mit/package.json", "{ \"name\": \"com.test.mit\", \"displayName\": \"Mit Tool\", \"version\": \"1.0.0\", \"license\": \"MIT\" }");
        Write(root, "Project/Packages/com.test.mit/LICENSE.md", "MIT License\nPermission is hereby granted, free of charge, to any person.\n");
        Write(root, "Project/Packages/com.test.gpl/package.json", "{ \"name\": \"com.test.gpl\", \"displayName\": \"Gpl Tool\", \"version\": \"2.0.0\", \"license\": \"GPL-3.0-only\" }");
        Write(root, "Project/Packages/com.test.none/package.json", "{ \"name\": \"com.test.none\", \"displayName\": \"Bare Tool\" }");
        Write(root, "Project/Library/ArtifactDB", new string('l', 2048));
        Write(root, "Project/Library/PackageCache/com.unity.thing@1.0.0/package.json", "{ \"name\": \"com.unity.thing\", \"displayName\": \"Unity Thing\", \"version\": \"1.0.0\" }");
        Write(root, "Project/Temp/temp.bin", new string('t', 512));
        Write(root, "Project/Logs/import.log", new string('g', 256));
        Write(root, "Project/.basisdev/com.test.loose/package.json", "{}");
        var manifest = Path.Combine(root, "Project", "Packages", "manifest.json");
        File.WriteAllText(manifest, "{ \"dependencies\": { \"com.unity.thing\": \"1.0.0\" } }");
        return new BasisInstall { RepoRoot = Path.Combine(root, "Project"), UnityProjectPath = Path.Combine(root, "Project"), Name = "Project", HasUnityProject = true };
    }

    private static ProjectStorageService Service(string root)
    {
        var git = new GitService();
        var projects = new UnityProjectService();
        var registry = new MountRegistry(Path.Combine(root, "data"));
        var mounts = new MountService(git, projects, registry);
        return new ProjectStorageService(new BasisDevService(git, projects, registry, mounts), mounts);
    }

    [AvaloniaFact]
    public async Task Licenses_list_every_package_and_count_the_concerns()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            var install = Project(root);
            var licenses = new LicensesViewModel(install.UnityProjectPath, "Test Project");

            await licenses.LoadAsync();

            Assert.Equal(new[] { "Bare Tool", "Gpl Tool", "Mit Tool" }, licenses.Rows.Select(r => r.Name));
            Assert.Equal("Packages: 3. Without a license: 1. Copyleft: 1.", licenses.SummaryText);
            Assert.True(licenses.HasWarnings);
            Assert.True(licenses.CanExport);
            var bare = licenses.Rows.Single(r => r.Id == "com.test.none");
            Assert.True(bare.IsMissing);
            Assert.Equal("No license stated", bare.LicenseText);
            Assert.Equal("com.test.mit \u00b7 1.0.0", licenses.Rows.Single(r => r.Id == "com.test.mit").Detail);
            Assert.True(licenses.Rows.Single(r => r.Id == "com.test.gpl").IsCopyleft);
            Assert.StartsWith("# Third-party notices", licenses.Markdown);

            licenses.IncludeUnity = true;
            await licenses.LoadAsync();
            Assert.Contains(licenses.Rows, r => r.Id == "com.unity.thing");

            licenses.ReportSaved("C:/notices.md");
            Assert.True(licenses.HasInfoStatus);
            licenses.ReportError("nope");
            Assert.True(licenses.HasErrorStatus);
            Assert.False(licenses.HasInfoStatus);
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public async Task Storage_measures_the_areas_and_asks_before_cleaning()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            var install = Project(root);
            var answer = false;
            var asked = new List<string>();
            var reported = new List<(string Message, bool Ok)>();
            var storage = new ProjectStorageViewModel(install, "Test Project", Service(root), null,
                (title, _) => { asked.Add(title); return Task.FromResult(answer); }, (message, ok) => reported.Add((message, ok)));

            await storage.MeasureAsync();

            Assert.Equal("Storage: Test Project", storage.Title);
            Assert.Equal(new[] { "Library", "Temp", "Logs", "Development clones", "Everything else" }, storage.Rows.Select(r => r.Label));
            var library = storage.Rows[0];
            Assert.True(library.HasDangerAction);
            Assert.Equal(DiskSpace.Human(2048 + new FileInfo(Path.Combine(install.UnityProjectPath, "Library", "PackageCache", "com.unity.thing@1.0.0", "package.json")).Length), library.SizeText);
            Assert.True(storage.Rows[1].HasSafeAction);
            Assert.False(storage.Rows[4].HasAction);
            Assert.Equal("Not a git clone", Assert.Single(storage.Clones).State);
            Assert.False(storage.HasUnusedClones);
            Assert.StartsWith(DiskSpace.Human(storage.Report!.TotalBytes) + " in ", storage.ProgressText);

            await storage.CleanLibraryAsync();
            Assert.Equal(new[] { "Clean Library?" }, asked);
            Assert.True(Directory.Exists(Path.Combine(install.UnityProjectPath, "Library")));

            answer = true;
            await storage.CleanLibraryAsync();
            Assert.False(Directory.Exists(Path.Combine(install.UnityProjectPath, "Library")));
            Assert.True(storage.HasInfoStatus);
            Assert.StartsWith("Freed ", storage.StatusText);
            Assert.Equal(storage.StatusText, Assert.Single(reported).Message);
            Assert.DoesNotContain(storage.Rows, r => r.Label == "Library");

            await storage.ClearLogsAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(install.UnityProjectPath, "Logs")));
        }
        finally { Delete(root); }
    }

    [AvaloniaFact]
    public async Task Storage_refuses_to_clean_while_Unity_has_the_project_open()
    {
        Localizer.Instance.SetLanguage("en");
        var root = NewRoot();
        try
        {
            var install = Project(root);
            var asked = 0;
            var storage = new ProjectStorageViewModel(install, "Test Project", Service(root), null, (_, _) => { asked++; return Task.FromResult(true); });
            await storage.MeasureAsync();
            var lockFile = Path.Combine(install.UnityProjectPath, "Temp", "UnityLockfile");
            File.WriteAllText(lockFile, "");

            using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await storage.CleanLibraryAsync();
                await storage.ClearTempAsync();
            }

            Assert.Equal(0, asked);
            Assert.True(storage.HasErrorStatus);
            Assert.Equal("Unity has Test Project open. Close Unity, then try again.", storage.StatusText);
            Assert.True(Directory.Exists(Path.Combine(install.UnityProjectPath, "Library")));
        }
        finally { Delete(root); }
    }
}
