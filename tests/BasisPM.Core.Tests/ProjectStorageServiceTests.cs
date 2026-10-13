using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class ProjectStorageServiceTests
{
    private static BasisInstall MakeInstall(TempDir t)
    {
        t.WriteFile("Repo/Basis/ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.25f1\n");
        t.WriteFile("Repo/Basis/Assets/Scene.unity", new string('a', 100));
        t.WriteFile("Repo/Basis/Packages/manifest.json", "{ \"dependencies\": { } }");
        t.WriteFile("Repo/Basis/Library/ArtifactDB", new string('l', 1000));
        t.WriteFile("Repo/Basis/Library/PackageCache/com.unity.x@1/file.bin", new string('p', 500));
        t.WriteFile("Repo/Basis/Temp/temp.bin", new string('t', 300));
        t.WriteFile("Repo/Basis/Logs/AssetImportWorker0.log", new string('g', 200));
        t.WriteFile("Repo/Basis/obj/Debug/x.dll", new string('o', 50));
        t.WriteFile("Repo/Basis/.basisdev/com.test.a/package.json", new string('c', 40));
        t.WriteFile("Repo/Basis/.basisdev/com.test.b/package.json", new string('d', 60));
        t.WriteFile("Repo/Basis/.basisdev/com.test.a.json", "{}");
        t.WriteFile("Repo/Basis Server/BasisServerConsole/Program.cs", new string('s', 70));
        t.WriteFile("Repo/Basis Server/BasisServerConsole/bin/Release/net10.0/server.dll", new string('b', 400));
        t.WriteFile("Repo/Basis Server/BasisNetworkCore/obj/project.assets.json", new string('j', 80));
        t.WriteFile("Repo/README.md", new string('r', 10));
        return new BasisInstall { RepoRoot = t.Combine("Repo"), UnityProjectPath = t.Combine("Repo", "Basis"), Name = "Repo", HasUnityProject = true };
    }

    private static ProjectStorageService Service(GitService? git = null, string? dataDir = null)
    {
        git ??= new GitService();
        var projects = new UnityProjectService();
        var registry = new MountRegistry(dataDir ?? Path.Combine(Path.GetTempPath(), "basispm-tests", "storage-" + Guid.NewGuid().ToString("N")));
        var mounts = new MountService(git, projects, registry);
        return new ProjectStorageService(new BasisDevService(git, projects, registry, mounts), mounts);
    }

    [Fact]
    public async Task Measure_splits_the_project_into_areas()
    {
        using var t = new TempDir("storage");
        var install = MakeInstall(t);
        var progress = new List<StorageProgress>();

        var report = await Service().MeasureAsync(install, new SyncProgress(progress.Add));

        Assert.Equal(1500, report.BytesOf(StorageArea.Library));
        Assert.Equal(500, report.BytesOf(StorageArea.PackageCache));
        Assert.Equal(300, report.BytesOf(StorageArea.Temp));
        Assert.Equal(200, report.BytesOf(StorageArea.Logs));
        Assert.Equal(50, report.BytesOf(StorageArea.Obj));
        Assert.Equal(480, report.BytesOf(StorageArea.ServerBuild));
        Assert.Equal(new[] { ("com.test.b", 60L), ("com.test.a", 40L) }, report.Clones.Select(c => (c.Name!, c.Bytes)));
        var total = 100 + 23 + 1000 + 500 + 300 + 200 + 50 + 40 + 60 + 2 + 70 + 400 + 80 + 10 + "m_EditorVersion: 6000.0.25f1\n".Length;
        Assert.Equal(total, report.TotalBytes);
        Assert.Equal(total - 1500 - 300 - 200 - 50 - 480 - 100, report.BytesOf(StorageArea.Other));
        Assert.Equal(15, report.TotalFiles);
        Assert.NotEmpty(progress);
        Assert.Equal(report.TotalBytes, progress[^1].Bytes);
    }

    [Fact]
    public async Task Measure_stops_when_cancelled()
    {
        using var t = new TempDir("storage");
        var install = MakeInstall(t);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().MeasureAsync(install, null, cancel.Token));
    }

    [Fact]
    public async Task Cleaning_deletes_Library_Temp_and_Logs()
    {
        using var t = new TempDir("storage");
        var install = MakeInstall(t);
        t.WriteFile("Repo/Basis/Library/ReadOnly.bin", "locked down");
        File.SetAttributes(t.Combine("Repo/Basis/Library/ReadOnly.bin"), FileAttributes.ReadOnly);

        var library = await ProjectStorageService.CleanLibraryAsync(install);
        var temp = await ProjectStorageService.ClearTempAsync(install);
        var logs = await ProjectStorageService.ClearLogsAsync(install);

        Assert.True(library.Ok, library.Message);
        Assert.Equal(1500 + "locked down".Length, library.Freed);
        Assert.False(Directory.Exists(t.Combine("Repo/Basis/Library")));
        Assert.True(temp.Ok, temp.Message);
        Assert.False(Directory.Exists(t.Combine("Repo/Basis/Temp")));
        Assert.True(logs.Ok, logs.Message);
        Assert.True(Directory.Exists(t.Combine("Repo/Basis/Logs")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(t.Combine("Repo/Basis/Logs")));
        Assert.True(File.Exists(t.Combine("Repo/Basis/Assets/Scene.unity")));
        Assert.True((await ProjectStorageService.CleanLibraryAsync(install)).Ok);
    }

    [Fact]
    public async Task Library_and_Temp_are_left_alone_while_Unity_has_the_project_open()
    {
        using var t = new TempDir("storage");
        var install = MakeInstall(t);
        var lockFile = t.WriteFile("Repo/Basis/Temp/UnityLockfile", "");

        using (new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var library = await ProjectStorageService.CleanLibraryAsync(install);
            var temp = await ProjectStorageService.ClearTempAsync(install);
            Assert.False(library.Ok);
            Assert.Contains("Close Unity", library.Message);
            Assert.False(temp.Ok);
        }

        Assert.True(Directory.Exists(t.Combine("Repo/Basis/Library")));
        Assert.True(File.Exists(t.Combine("Repo/Basis/Temp/temp.bin")));
    }

    [WindowsFact]
    public async Task Logs_in_use_are_skipped()
    {
        using var t = new TempDir("storage");
        var install = MakeInstall(t);
        var busy = t.WriteFile("Repo/Basis/Logs/shadercompiler.log", "busy");

        StorageCleanResult result;
        using (new FileStream(busy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = await ProjectStorageService.ClearLogsAsync(install);

        Assert.False(result.Ok);
        Assert.Equal(200, result.Freed);
        Assert.Equal(new[] { busy }, result.Skipped);
        Assert.False(File.Exists(t.Combine("Repo/Basis/Logs/AssetImportWorker0.log")));
    }

    [GitFact]
    public async Task Only_unused_clones_without_local_work_are_removed()
    {
        using var sandbox = new GitSandbox();
        sandbox.CommitUpstream("package", ("package.json", "{ \"name\": \"com.test.pkg\", \"version\": \"1.0.0\" }\n"));
        var unity = sandbox.Combine("Project");
        GitSandbox.Write(unity, "ProjectSettings/ProjectVersion.txt", "m_EditorVersion: 6000.0.25f1\n");
        Directory.CreateDirectory(Path.Combine(unity, "Assets"));
        GitSandbox.Write(unity, "Packages/manifest.json", "{ \"dependencies\": { \"com.test.used\": \"file:../.basisdev/com.test.used\" } }");
        foreach (var id in new[] { "com.test.unused", "com.test.edited", "com.test.listed", "com.test.used" })
        {
            GitSandbox.Run(sandbox.Combine(), "clone", "-q", sandbox.Upstream, Path.Combine(unity, ".basisdev", id));
            GitSandbox.Run(Path.Combine(unity, ".basisdev", id), "config", "core.autocrlf", "false");
        }
        GitSandbox.Write(Path.Combine(unity, ".basisdev", "com.test.edited"), "package.json", "{ \"name\": \"com.test.pkg\", \"version\": \"1.0.1\" }\n");
        BasisPackagesStore.Set(unity, "com.test.listed", new BasisPackageEntry { Url = "https://github.com/test/listed.git", Commit = "abc" });
        var install = new BasisInstall { RepoRoot = unity, UnityProjectPath = unity, Name = "Project", HasUnityProject = true };
        var service = Service(new GitService(allowLocalRemotes: true, GitRetryPolicy.None), sandbox.Combine("data"));

        var unused = await service.FindUnusedClonesAsync(install);

        Assert.Equal(new[] { "com.test.edited", "com.test.listed", "com.test.unused" }, unused.Select(c => c.Id));
        Assert.Equal(UnusedCloneReason.LocalWork, unused.Single(c => c.Id == "com.test.edited").Reason);
        Assert.Equal(UnusedCloneReason.Listed, unused.Single(c => c.Id == "com.test.listed").Reason);
        Assert.True(unused.Single(c => c.Id == "com.test.unused").Removable);

        var result = await service.RemoveUnusedClonesAsync(install);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { "com.test.unused" }, result.Removed);
        Assert.Equal(2, result.Skipped.Count);
        Assert.True(result.Freed > 0);
        Assert.False(Directory.Exists(Path.Combine(unity, ".basisdev", "com.test.unused")));
        Assert.True(Directory.Exists(Path.Combine(unity, ".basisdev", "com.test.edited")));
        Assert.True(Directory.Exists(Path.Combine(unity, ".basisdev", "com.test.listed")));
        Assert.True(Directory.Exists(Path.Combine(unity, ".basisdev", "com.test.used")));
        Assert.NotNull(BasisPackagesStore.Find(unity, "com.test.listed"));
    }

    private sealed class SyncProgress(Action<StorageProgress> report) : IProgress<StorageProgress>
    {
        public void Report(StorageProgress value) => report(value);
    }
}
