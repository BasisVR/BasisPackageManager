using Avalonia.Headless.XUnit;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.App.Tests;

public sealed class PackageRowCheckTests
{
    private static PackageRow Row(string? installed = "1.0.0") => new(new CatalogPackageVersion { Name = "com.example.pkg", DisplayName = "Example", Version = "1.0.0", Url = "https://github.com/example/pkg.git" }, installed);

    private static PackageCheckSummary Summary(PackageVerdict verdict, bool fromProject = true, bool stale = false, string? summary = null, int warnings = 0) =>
        new(verdict, fromProject, stale, new PackageCheckState { Verdict = verdict, Summary = summary, Warnings = warnings, BasisBranch = "developer", Checked = new DateTimeOffset(2026, 10, 14, 1, 2, 0, TimeSpan.Zero) });

    [AvaloniaFact]
    public void No_check_shows_nothing()
    {
        var row = Row();
        row.ApplyCheck(null, "developer", Array.Empty<PackageIssue>(), null, inProject: true);

        Assert.False(row.HasCheck);
        Assert.Equal("", row.CheckLabel);
    }

    [AvaloniaFact]
    public void A_project_result_that_works()
    {
        var row = Row();
        row.ApplyCheck(Summary(PackageVerdict.Works), "developer", Array.Empty<PackageIssue>(), null, inProject: true);

        Assert.True(row.HasCheck);
        Assert.True(row.CheckWorks);
        Assert.False(row.CheckBreaks);
        Assert.Equal("Works here", row.CheckLabel);
        Assert.Contains("developer", row.CheckTip);
    }

    [AvaloniaFact]
    public void A_project_result_that_breaks_lists_its_issues_errors_first()
    {
        var row = Row();
        var issues = new[]
        {
            new PackageIssue(IssueCodes.TypeDuplicate, IssueSeverity.Warning, "com.example.pkg", "dup"),
            new PackageIssue(IssueCodes.CompileError, IssueSeverity.Error, "com.example.pkg", "CS0246: missing") { File = "X.cs", Line = 3 },
        };
        row.ApplyCheck(Summary(PackageVerdict.Breaks, summary: "X.cs(3): CS0246: missing"), "developer", issues, null, inProject: true);

        Assert.True(row.CheckBreaks);
        Assert.Equal("Breaks this project", row.CheckLabel);
        Assert.Equal(IssueSeverity.Error, row.CheckIssues[0].Severity);
        Assert.True(row.HasCheckIssues);
        Assert.Contains("CS0246", row.CheckTip);
    }

    [AvaloniaFact]
    public void Registry_results_name_the_basis_channel_and_carry_no_local_issues()
    {
        var row = Row(installed: null);
        row.ApplyCheck(Summary(PackageVerdict.Breaks, fromProject: false), "experimental-branch-major-changes",
            new[] { new PackageIssue(IssueCodes.CompileError, IssueSeverity.Error, "com.example.pkg", "old local error") }, "C:/log.txt", inProject: false);

        Assert.True(row.HasCheck);
        Assert.Equal("Fails on Basis experimental", row.CheckLabel);
        Assert.False(row.HasCheckIssues);
        Assert.Null(row.CheckLogPath);
    }

    [AvaloniaFact]
    public void A_project_result_for_a_package_that_left_the_project_is_hidden()
    {
        var row = Row(installed: null);
        row.ApplyCheck(Summary(PackageVerdict.Works), "developer", Array.Empty<PackageIssue>(), null, inProject: false);

        Assert.False(row.HasCheck);
    }

    [AvaloniaFact]
    public void Stale_results_ask_for_another_check()
    {
        var row = Row();
        row.ApplyCheck(Summary(PackageVerdict.Works, stale: true), "long-term-support-20260916", Array.Empty<PackageIssue>(), null, inProject: true);

        Assert.False(row.CheckWorks);
        Assert.True(row.CheckWarns);
        Assert.Equal("Check again", row.CheckLabel);
    }

    [AvaloniaFact]
    public void Warnings_show_their_count()
    {
        var row = Row();
        row.ApplyCheck(Summary(PackageVerdict.Warnings, warnings: 2), "developer", Array.Empty<PackageIssue>(), null, inProject: true);

        Assert.True(row.CheckWarns);
        Assert.Equal("2 warnings", row.CheckLabel);
    }
}
