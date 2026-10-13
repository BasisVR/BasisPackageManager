using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageCheckStoreTests
{
    private static ProjectCheckReport Report(string project, params (string Id, PackageVerdict Verdict)[] packages)
    {
        var report = new ProjectCheckReport { Project = project, Started = DateTimeOffset.UtcNow, Finished = DateTimeOffset.UtcNow, Reason = "test" };
        foreach (var (id, verdict) in packages) report.Packages[id] = new PackageCheckState { Verdict = verdict, BasisCommit = "abc1234def" };
        return report;
    }

    [Fact]
    public void Saves_and_reloads_the_last_report_and_package_states()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var project = temp.CreateDir("Project");
        var report = Report(project, ("com.a", PackageVerdict.Works), ("com.b", PackageVerdict.Breaks));
        report.Issues.Add(new PackageIssue(IssueCodes.TypeDuplicate, IssueSeverity.Warning, "com.b", "Two types") { File = "Packages/com.b/X.cs", Line = 3 });
        report.Compile = new CompileCheckResult { Status = CompileCheckStatus.Failed, Diagnostics = { new CompileDiagnostic(CompileDiagnosticKind.Compiler, IssueSeverity.Error, "CS0246", "Missing") { Package = "com.b" } } };

        store.Save(report);
        var loaded = store.Load(project);

        Assert.NotNull(loaded);
        Assert.Equal(PackageVerdict.Works, loaded!.Packages["com.a"].Verdict);
        Assert.Equal(PackageVerdict.Breaks, loaded.Packages["COM.B"].Verdict);
        Assert.Equal(CompileCheckStatus.Failed, loaded.Last!.Compile!.Status);
        Assert.Equal("CS0246", loaded.Last.Compile.Diagnostics.Single().Code);
        Assert.Equal(3, loaded.Last.Issues.Single().Line);
        Assert.Equal(IssueSeverity.Warning, loaded.Last.Issues.Single().Severity);
    }

    [Fact]
    public void A_later_report_keeps_states_of_packages_it_did_not_check()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var project = temp.CreateDir("Project");

        store.Save(Report(project, ("com.a", PackageVerdict.Works)));
        store.Save(Report(project, ("com.b", PackageVerdict.Warnings)));

        var loaded = store.Load(project)!;
        Assert.Equal(PackageVerdict.Works, loaded.Packages["com.a"].Verdict);
        Assert.Equal(PackageVerdict.Warnings, loaded.Packages["com.b"].Verdict);
        Assert.Equal(PackageVerdict.Warnings, store.Find(project, "com.b")!.Verdict);
    }

    [Fact]
    public void Forget_drops_one_package()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var project = temp.CreateDir("Project");
        store.Save(Report(project, ("com.a", PackageVerdict.Works), ("com.b", PackageVerdict.Works)));

        store.Forget(project, "com.a");

        Assert.Null(store.Find(project, "com.a"));
        Assert.NotNull(store.Find(project, "com.b"));
    }

    [Fact]
    public void Projects_get_separate_files_and_the_key_ignores_trailing_separators()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var one = temp.CreateDir("One");
        var two = temp.CreateDir("Two");

        Assert.NotEqual(store.PathFor(one), store.PathFor(two));
        Assert.Equal(PackageCheckStore.KeyFor(one), PackageCheckStore.KeyFor(one + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows()) Assert.Equal(PackageCheckStore.KeyFor(one), PackageCheckStore.KeyFor(one.ToUpperInvariant()));
    }

    [Fact]
    public void An_unreadable_file_reads_as_nothing_and_is_kept_aside()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var project = temp.CreateDir("Project");
        Directory.CreateDirectory(store.Root);
        File.WriteAllText(store.PathFor(project), "{ not json");

        Assert.Null(store.Load(project));
        Assert.True(File.Exists(store.PathFor(project) + ".corrupt"));
    }

    [Fact]
    public void Long_issue_lists_are_trimmed_keeping_errors_first()
    {
        using var temp = new TempDir("checks");
        var store = new PackageCheckStore(temp.Combine("store"));
        var project = temp.CreateDir("Project");
        var report = Report(project);
        for (var i = 0; i < PackageCheckStore.MaxIssues + 50; i++)
            report.Issues.Add(new PackageIssue(IssueCodes.MetaMissing, IssueSeverity.Info, "com.a", "info " + i));
        report.Issues.Add(new PackageIssue(IssueCodes.AssemblyDuplicate, IssueSeverity.Error, "com.a", "error"));

        store.Save(report);

        var issues = store.Load(project)!.Last!.Issues;
        Assert.Equal(PackageCheckStore.MaxIssues, issues.Count);
        Assert.Equal(IssueSeverity.Error, issues[0].Severity);
    }
}
