using BasisPM.Core.Models;
using BasisPM.Core.Services;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class PackageCheckVerdictsTests
{
    private static ProjectCheckReport Report(CompileCheckStatus? compile, params PackageIssue[] issues)
    {
        var report = new ProjectCheckReport
        {
            Project = "P",
            Finished = DateTimeOffset.UtcNow,
            Basis = new BasisCheckTarget { Branch = "developer", Commit = "0d4695eb4e262fa5" },
            UnityVersion = "6000.7.0b4",
            Focus = { "com.new" },
        };
        report.Issues.AddRange(issues);
        if (compile is { } status) report.Compile = new CompileCheckResult { Status = status };
        return report;
    }

    private static PackageIssue Error(string package, string message = "boom") => new(IssueCodes.CompileError, IssueSeverity.Error, package, message) { File = $"Packages/{package}/X.cs", Line = 4, Column = 2 };

    private static PackageIssue Warning(string package) => new(IssueCodes.TypeDuplicate, IssueSeverity.Warning, package, "dup");

    private static InspectedPackage Package(string id, params AsmdefInfo[] assemblies)
    {
        var package = new InspectedPackage { Ref = new ProjectPackageRef(id, "/p/" + id, id == UnityPackageMap.AssetsId ? PackageOrigin.Project : PackageOrigin.Local) };
        package.Assemblies.AddRange(assemblies);
        return package;
    }

    private static AsmdefInfo Asmdef(string name, string? guid = null, bool editorOnly = false, string[]? references = null) => new()
    {
        Name = name,
        FullPath = $"/p/{name}.asmdef",
        RelativePath = name + ".asmdef",
        Guid = guid,
        References = references ?? Array.Empty<string>(),
        IncludePlatforms = editorOnly ? new[] { "Editor" } : Array.Empty<string>(),
    };

    private static CompileCheckResult Failed(string assembly) => new()
    {
        Status = CompileCheckStatus.Failed,
        FailedAssemblies = { assembly },
        Diagnostics = { new CompileDiagnostic(CompileDiagnosticKind.Compiler, IssueSeverity.Error, "CS0619", "obsolete") { Assembly = assembly } },
    };

    [Fact]
    public void A_clean_compile_means_it_works()
    {
        var state = PackageCheckVerdicts.StateFor(Report(CompileCheckStatus.Passed), "com.new", "1.0.0", "abc1234");

        Assert.Equal(PackageVerdict.Works, state.Verdict);
        Assert.True(state.Compiled);
        Assert.Equal("developer", state.BasisBranch);
        Assert.Equal("1.0.0", state.Version);
        Assert.Null(state.Summary);
    }

    [Fact]
    public void Its_own_errors_mean_it_breaks_with_the_first_error_as_summary()
    {
        var state = PackageCheckVerdicts.StateFor(Report(CompileCheckStatus.Failed, Error("com.new", "CS0246 thing")), "com.new");

        Assert.Equal(PackageVerdict.Breaks, state.Verdict);
        Assert.Equal(1, state.Errors);
        Assert.Equal("Packages/com.new/X.cs(4,2): CS0246 thing", state.Summary);
    }

    [Fact]
    public void New_errors_elsewhere_are_blamed_on_the_package_being_installed()
    {
        var report = Report(CompileCheckStatus.Failed, Error("Assets"));
        report.Introduced.Add(PackageCheckKeys.Of(report.Issues[0]));

        Assert.Equal(PackageVerdict.Breaks, PackageCheckVerdicts.StateFor(report, "com.new").Verdict);
        Assert.Equal(PackageVerdict.Unknown, PackageCheckVerdicts.StateFor(report, "com.other").Verdict);
    }

    [Fact]
    public void Old_errors_elsewhere_leave_the_package_unknown()
    {
        var state = PackageCheckVerdicts.StateFor(Report(CompileCheckStatus.Failed, Error("Assets")), "com.new");

        Assert.Equal(PackageVerdict.Unknown, state.Verdict);
        Assert.False(state.Compiled);
    }

    [Fact]
    public void Old_errors_in_another_package_dont_hide_a_package_that_compiled()
    {
        var report = Report(CompileCheckStatus.Failed, Error("com.broken"));
        report.Uncompiled = new List<string> { "com.broken" };

        var state = PackageCheckVerdicts.StateFor(report, "com.new");

        Assert.Equal(PackageVerdict.Works, state.Verdict);
        Assert.True(state.Compiled);
        Assert.Equal(PackageVerdict.Breaks, PackageCheckVerdicts.StateFor(report, "com.broken").Verdict);
    }

    [Fact]
    public void A_package_that_needs_a_broken_assembly_stays_unknown()
    {
        var report = Report(CompileCheckStatus.Failed, Error("com.broken"));
        report.Uncompiled = new List<string> { "com.broken", "com.new" };

        var state = PackageCheckVerdicts.StateFor(report, "com.new");

        Assert.Equal(PackageVerdict.Unknown, state.Verdict);
        Assert.False(state.Compiled);
    }

    [Fact]
    public void Uncompiled_follows_references_asmrefs_and_the_predefined_assemblies()
    {
        var extends = Package("com.extends");
        extends.Sources.Add(new InventorySource { FullPath = "/p/com.extends/X.cs", RelativePath = "X.cs", AsmrefReference = "GUID:b0" });
        var assets = Package(UnityPackageMap.AssetsId);
        assets.Sources.Add(new InventorySource { FullPath = "/p/Assets/Game.cs", RelativePath = "Game.cs", Assembly = "Assembly-CSharp" });
        var inventory = new ProjectInventory
        {
            UnityProjectPath = "/p",
            Packages = new[]
            {
                Package("com.broken", Asmdef("Broken.Editor", "b0", editorOnly: true)),
                Package("com.needs", Asmdef("Needs.Editor", editorOnly: true, references: new[] { "GUID:b0" })),
                Package("com.through", Asmdef("Through.Editor", editorOnly: true, references: new[] { "Needs.Editor" })),
                Package("com.free", Asmdef("Free"), Asmdef("Free.Editor", editorOnly: true)),
                extends,
                assets,
            },
        };

        Assert.Equal(new[] { "com.broken", "com.needs", "com.through", "com.extends" }, PackageCheckVerdicts.Uncompiled(inventory, Failed("Broken.Editor")));

        assets.Sources.Add(new InventorySource { FullPath = "/p/Assets/Editor/Tool.cs", RelativePath = "Editor/Tool.cs", Assembly = "Assembly-CSharp-Editor" });
        Assert.Contains(UnityPackageMap.AssetsId, PackageCheckVerdicts.Uncompiled(inventory, Failed("Broken.Editor"))!);
        Assert.Equal(new[] { "com.free", UnityPackageMap.AssetsId }, PackageCheckVerdicts.Uncompiled(inventory, Failed("Free")));
    }

    [Fact]
    public void Uncompiled_is_unknown_unless_every_error_names_its_assembly()
    {
        var inventory = new ProjectInventory { UnityProjectPath = "/p", Packages = new[] { Package("com.free", Asmdef("Free")) } };
        var unattributed = new CompileCheckResult
        {
            Status = CompileCheckStatus.Failed,
            Diagnostics = { new CompileDiagnostic(CompileDiagnosticKind.Other, IssueSeverity.Error, "", "Unity stopped") },
        };

        Assert.Null(PackageCheckVerdicts.Uncompiled(inventory, unattributed));
        Assert.Null(PackageCheckVerdicts.Uncompiled(inventory, new CompileCheckResult { Status = CompileCheckStatus.Failed }));
        Assert.Null(PackageCheckVerdicts.Uncompiled(inventory, new CompileCheckResult { Status = CompileCheckStatus.Passed }));
        Assert.Null(PackageCheckVerdicts.Uncompiled(inventory, null));
    }

    [Fact]
    public void New_errors_only_count_against_a_package_the_failing_code_can_see()
    {
        var inventory = new ProjectInventory
        {
            UnityProjectPath = "/p",
            Packages = new[]
            {
                Package("com.new", Asmdef("New")),
                Package("com.local", Asmdef("Local")),
                Package("com.user", Asmdef("User", references: new[] { "New" })),
                Package(UnityPackageMap.AssetsId),
            },
        };
        var unrelated = Error("com.local") with { Other = "Local" };
        var dependent = Error("com.user") with { Other = "User" };
        var scripts = Error(UnityPackageMap.AssetsId) with { Other = "Assembly-CSharp" };
        var report = Report(CompileCheckStatus.Failed, unrelated, dependent, scripts);
        report.Introduced.AddRange(report.Issues.Select(PackageCheckKeys.Of));

        report.Attributed = PackageCheckVerdicts.Attribute(report, inventory);

        Assert.Equal(new[] { PackageCheckKeys.Of(dependent), PackageCheckKeys.Of(scripts) }, report.Attributed!["com.new"]);
        var state = PackageCheckVerdicts.StateFor(report, "com.new");
        Assert.Equal(PackageVerdict.Breaks, state.Verdict);
        Assert.Equal(2, state.Errors);
    }

    [Fact]
    public void A_new_error_in_code_that_cant_see_the_package_leaves_it_working()
    {
        var inventory = new ProjectInventory { UnityProjectPath = "/p", Packages = new[] { Package("com.new", Asmdef("New")), Package("com.local", Asmdef("Local")) } };
        var report = Report(CompileCheckStatus.Failed, Error("com.local") with { Other = "Local" });
        report.Introduced.Add(PackageCheckKeys.Of(report.Issues[0]));
        report.Uncompiled = PackageCheckVerdicts.Uncompiled(inventory, Failed("Local"));
        report.Attributed = PackageCheckVerdicts.Attribute(report, inventory);

        var state = PackageCheckVerdicts.StateFor(report, "com.new");

        Assert.Equal(PackageVerdict.Works, state.Verdict);
        Assert.True(state.Compiled);
    }

    [Fact]
    public void A_package_with_its_own_dlls_is_suspected_of_new_errors_that_name_them()
    {
        var package = Package("com.new", Asmdef("New"));
        package.Plugins.Add(new InventoryPlugin { FileName = "Lib.dll", FullPath = "/p/com.new/Lib.dll", RelativePath = "Lib.dll", IsManaged = true, AssemblyName = "Lib", Namespaces = new[] { "Acme.Lib" }, PublicTypes = new[] { "Acme.Lib.Thing" } });
        var inventory = new ProjectInventory { UnityProjectPath = "/p", Packages = new[] { package, Package("com.local", Asmdef("Local")) } };
        var named = Error("com.local", "CS0433: The type 'Thing' exists in both 'Lib, Version=1.0.0.0' and 'Other, Version=2.0.0.0'") with { Other = "Local" };
        var unrelated = Error("com.local", "CS0029: Cannot implicitly convert type 'int' to 'bool'") with { Other = "Local", Line = 9 };
        var report = Report(CompileCheckStatus.Failed, named, unrelated);
        report.Introduced.AddRange(report.Issues.Select(PackageCheckKeys.Of));

        Assert.Equal(new[] { PackageCheckKeys.Of(named) }, PackageCheckVerdicts.Attribute(report, inventory)!["com.new"]);
    }

    [Fact]
    public void Warnings_without_errors_and_static_only_runs()
    {
        Assert.Equal(PackageVerdict.Warnings, PackageCheckVerdicts.StateFor(Report(CompileCheckStatus.Passed, Warning("com.new")), "com.new").Verdict);
        Assert.Equal(PackageVerdict.Warnings, PackageCheckVerdicts.StateFor(Report(null, Warning("com.new")), "com.new").Verdict);
        Assert.Equal(PackageVerdict.Unknown, PackageCheckVerdicts.StateFor(Report(null), "com.new").Verdict);
        Assert.Equal(PackageVerdict.Breaks, PackageCheckVerdicts.StateFor(Report(null, Error("com.new")), "com.new").Verdict);
    }

    [Fact]
    public void Local_results_win_unless_they_are_stale_and_the_registry_is_fresh()
    {
        var local = new PackageCheckState { Verdict = PackageVerdict.Works, BasisCommit = "aaaaaaa1111", Commit = "ccccccc" };
        var registry = new PackageCheckState { Verdict = PackageVerdict.Breaks, BasisCommit = "bbbbbbb2222", Commit = "ccccccc" };

        var fresh = PackageCheckVerdicts.Summarize(local, registry, projectBasisCommit: "aaaaaaa1111", packageCommit: "ccccccc", registryHeadNow: "bbbbbbb2222")!;
        Assert.True(fresh.FromProject);
        Assert.False(fresh.Stale);

        var afterUpdate = PackageCheckVerdicts.Summarize(local, registry, projectBasisCommit: "ddddddd3333", packageCommit: "ccccccc", registryHeadNow: "bbbbbbb2222")!;
        Assert.False(afterUpdate.FromProject);
        Assert.Equal(PackageVerdict.Breaks, afterUpdate.Verdict);

        var bothStale = PackageCheckVerdicts.Summarize(local, registry, projectBasisCommit: "ddddddd3333", packageCommit: "ccccccc", registryHeadNow: "eeeeeee4444")!;
        Assert.True(bothStale.FromProject);
        Assert.True(bothStale.Stale);

        Assert.Null(PackageCheckVerdicts.Summarize(null, null));
        Assert.False(PackageCheckVerdicts.Summarize(null, registry)!.FromProject);
    }

    [Theory]
    [InlineData("0d4695eb4e262fa59449f57669d6a07580b82171", "0d4695eb4", true)]
    [InlineData("0D4695EB4E262FA5", "0d4695eb4e262fa5", true)]
    [InlineData("0d4695e", "0d4695eb4", true)]
    [InlineData("0d4695", "0d4695eb4", false)]
    [InlineData("0d4695eb4", "51fa698ed", false)]
    [InlineData(null, "51fa698ed", false)]
    public void Commits_match_by_prefix(string? a, string? b, bool same) => Assert.Equal(same, PackageCheckVerdicts.SameCommit(a, b));
}
