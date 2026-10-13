using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityLogParserTests
{
    private const string Project = @"C:\Users\doola\AppData\Local\Temp\claude\scratchpad\unityprobe\Probe";

    private const string MissingDependencyLog = """
        [Package Manager] Done registering packages in 0.01 seconds
        [Package Manager] Done resolving packages in 0.10 seconds
        [Licensing::Client] Successfully resolved entitlement details
        An error occurred while resolving packages:
          Package com.test.bad@file:C:\Users\doola\AppData\Local\Temp\claude\scratchpad\unityprobe\Probe\Packages\com.test.bad has invalid dependencies or related test packages:
            com.test.missing (dependency): Package [com.test.missing@1.2.3] cannot be found
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string ProjectDependenciesLog = """
        An error occurred while resolving packages:
          Project has invalid dependencies:
            com.basispm.gitmissing: Error when executing git command. remote: Repository not found.
            fatal: repository 'https://github.com/basispm-tests/does-not-exist-9f.git/' not found

            com.unity.nonexistent: Package [com.unity.nonexistent@1.0.0] cannot be found
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string GitDependencyLog = """
        An error occurred while resolving packages:
          Package com.test.dup@file:C:\Users\doola\My Projects\Probe\Packages\com.test.dup has invalid dependencies or related test packages:
            com.example.gitdep (dependency): Version 'https://github.com/example/gitdep.git' is invalid. Expected a value that follows semantic versioning rules.
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string CompileErrorLog = """
        [ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed
        Starting: C:\Program Files\Unity\Hub\Editor\6000.7.0b4\Editor\Data\Tools\BuildPipeline\bee_backend.exe --ipc --defer-dag-verification --dagfile="Library/Bee/1900b0aE.dag" --continue-on-failure --profile="Library/Bee/backend1.traceevents" ScriptAssemblies
        *** Tundra requires additional run (0.11 seconds), 1 items updated, 206 evaluated
        [           0s] Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others) [CacheMiss 73586151274f6abeb41c0c8bbb8baa3500000000000000000000000000000006]
        [234/246    0s] Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others)
        ##### ExitCode
        1
        ##### Output
        Packages\com.test.bad\Runtime\Broken.cs(5,28): error CS0246: The type or namespace name 'NotAType' could not be found (are you missing a using directive or an assembly reference?)
        *** Tundra build failed (0.39 seconds), 22 items updated, 246 evaluated
        ## Script Compilation Error for: Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others)
        ## CmdLine: "C:\Program Files\Unity\Hub\Editor\6000.7.0b4\Editor\Data\DotNetSdk\dotnet.exe" exec "C:/Program Files/Unity/Hub/Editor/6000.7.0b4/Editor/Data/DotNetSdk/sdk/10.0.303/Roslyn/bincore/csc.dll" /nostdlib /noconfig /shared "@Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.rsp"
        ## Output:
        Packages\com.test.bad\Runtime\Broken.cs(5,28): error CS0246: The type or namespace name 'NotAType' could not be found (are you missing a using directive or an assembly reference?)

        Packages\com.test.bad\Runtime\Broken.cs(5,28): error CS0246: The type or namespace name 'NotAType' could not be found (are you missing a using directive or an assembly reference?)
        AssetDatabase: script compilation time: 1.306410s
        Scripts have compiler errors.
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string UserCodeLog = """
        Assets\UserScript.cs(2,64): error CS0117: 'Debug' does not contain a definition for 'Log'
        Assets\UserScript.cs(2,94): error CS0433: The type 'Thing' exists in both 'Com.Test.Bad, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' and 'Com.Test.Dup, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'
        *** Tundra build failed (0.44 seconds), 19 items updated, 256 evaluated
        ## Script Compilation Error for: Csc Library/Bee/artifacts/1900b0aE.dag/Assembly-CSharp.dll (+2 others)
        ## Output:
        Assets\UserScript.cs(2,64): error CS0117: 'Debug' does not contain a definition for 'Log'
        Assets\UserScript.cs(2,94): error CS0433: The type 'Thing' exists in both 'Com.Test.Bad, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null' and 'Com.Test.Dup, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null'
        Scripts have compiler errors.
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string DuplicateAssemblyLog = """
        ADB: OOP importers: queue 1500 composition: PrefabImporter (only the named importers above run OOP; the rest stay in-process)
        Assembly with name 'Com.Test.Bad' already exists (Packages/com.test.bad/Runtime/Com.Test.Bad.asmdef)
        Assembly with name 'Com.Test.Bad' already exists (Packages/com.test.dup/Runtime2/Dup2.asmdef)
        Script 'Packages/com.test.bad/NoAsmdef/Loose.cs' will not be compiled because it exists outside the Assets folder and does not to belong to any assembly definition file.
        [ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed
        AssetDatabase: script compilation time: 0.042888s
        Scripts have compiler errors.
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
        """;

    private const string SuccessLog = """
        [Licensing::Client] Code 10 while verifying Licensing Client signature (process Id: 20528, path: "C:/Program Files/Unity Hub/UnityLicensingClient_V1/Unity.Licensing.Client.exe")
        [Licensing::Module] LicensingClient has failed validation; ignoring
        [Licensing::Client] Error: HandshakeResponse reported an error:
        [Licensing::Module] Error: Failed to handshake to channel: "LicenseClient-doola"
        [Licensing::Module] Error: Access token is unavailable; failed to update
        [Licensing::Client] Successfully resolved entitlement details
        [Licensing::Module] License group:
        Script 'Packages/com.test.bad/NoAsmdef/Loose.cs' will not be compiled because it exists outside the Assets folder and does not to belong to any assembly definition file.
        [ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed
        Starting: C:\Program Files\Unity\Hub\Editor\6000.7.0b4\Editor\Data\Tools\BuildPipeline\bee_backend.exe --ipc --defer-dag-verification --dagfile="Library/Bee/1900b0aE.dag" --continue-on-failure ScriptAssemblies
        *** Tundra requires additional run (0.14 seconds), 2 items updated, 233 evaluated
        Starting: C:\Program Files\Unity\Hub\Editor\6000.7.0b4\Editor\Data\Tools\BuildPipeline\bee_backend.exe --ipc --defer-dag-verification --dagfile="Library/Bee/1900b0aE.dag" --continue-on-failure ScriptAssemblies
        *** Tundra build success (0.64 seconds), 25 items updated, 256 evaluated
        abort_threads: Failed aborting id: 0000000000013A88, mono_thread_manage will ignore it
        Exiting batchmode successfully now!
        [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 0
        """;

    private const string ProjectOpenOutput = """
        VBCSCompilerPreWarmer: watching VBCSCompiler (PID 78524)

        Aborting batchmode due to fatal error:
        It looks like another Unity instance is running with this project open.

        Multiple Unity instances cannot open the same project.

        Project: C:/Users/doola/AppData/Local/Temp/claude/scratchpad/unityprobe/Probe
        """;

    [Fact]
    public void Reports_a_package_with_a_missing_dependency()
    {
        var summary = UnityLogParser.Parse(MissingDependencyLog, Project);

        Assert.True(summary.PackageResolutionFailed);
        var problem = Assert.Single(summary.Diagnostics);
        Assert.Equal(CompileDiagnosticKind.PackageResolution, problem.Kind);
        Assert.Equal(IssueSeverity.Error, problem.Severity);
        Assert.Equal(IssueCodes.PackageResolution, problem.Code);
        Assert.Equal("com.test.bad", problem.Package);
        Assert.Equal("com.test.missing (dependency): Package [com.test.missing@1.2.3] cannot be found", problem.Message);
        Assert.Equal(1, summary.ExitCode);
        Assert.False(summary.LicenseFailed);
    }

    [Fact]
    public void Reports_each_invalid_project_dependency_and_joins_continuation_lines()
    {
        var summary = UnityLogParser.Parse(ProjectDependenciesLog, Project);

        Assert.True(summary.PackageResolutionFailed);
        Assert.Equal(2, summary.Diagnostics.Count);
        var git = summary.Diagnostics[0];
        Assert.Equal("com.basispm.gitmissing", git.Package);
        Assert.StartsWith("com.basispm.gitmissing: Error when executing git command. remote: Repository not found.", git.Message);
        Assert.Contains("fatal: repository 'https://github.com/basispm-tests/does-not-exist-9f.git/' not found", git.Message);
        Assert.Equal("com.unity.nonexistent", summary.Diagnostics[1].Package);
        Assert.Equal("com.unity.nonexistent: Package [com.unity.nonexistent@1.0.0] cannot be found", summary.Diagnostics[1].Message);
    }

    [Fact]
    public void Reports_a_git_url_in_package_json_dependencies_even_when_the_path_has_spaces()
    {
        var summary = UnityLogParser.Parse(GitDependencyLog, Project);

        var problem = Assert.Single(summary.Diagnostics);
        Assert.Equal("com.test.dup", problem.Package);
        Assert.Contains("Version 'https://github.com/example/gitdep.git' is invalid", problem.Message);
    }

    [Fact]
    public void Reads_compile_errors_once_with_their_assembly()
    {
        var summary = UnityLogParser.Parse(CompileErrorLog, Project);

        var error = Assert.Single(summary.Diagnostics);
        Assert.Equal(CompileDiagnosticKind.Compiler, error.Kind);
        Assert.Equal(IssueSeverity.Error, error.Severity);
        Assert.Equal("CS0246", error.Code);
        Assert.Equal("Packages/com.test.bad/Runtime/Broken.cs", error.File);
        Assert.Equal(5, error.Line);
        Assert.Equal(28, error.Column);
        Assert.Equal("Com.Test.Bad", error.Assembly);
        Assert.StartsWith("The type or namespace name 'NotAType' could not be found", error.Message);
        Assert.Equal(new[] { "Com.Test.Bad" }, summary.FailedAssemblies);
        Assert.True(summary.CompilerErrors);
        Assert.Equal(1, summary.Builds);
        Assert.False(summary.LastBuildSucceeded);
        Assert.True(summary.HasErrors);
        Assert.Equal(1, summary.ExitCode);
    }

    [Fact]
    public void Reads_errors_a_package_causes_in_project_scripts()
    {
        var summary = UnityLogParser.Parse(UserCodeLog, Project);

        Assert.Equal(2, summary.Diagnostics.Count);
        Assert.All(summary.Diagnostics, d => Assert.Equal("Assets/UserScript.cs", d.File));
        Assert.Equal(new[] { "CS0117", "CS0433" }, summary.Diagnostics.Select(d => d.Code));
        Assert.Contains("exists in both 'Com.Test.Bad", summary.Diagnostics[1].Message);
        Assert.Equal(new[] { "Assembly-CSharp" }, summary.FailedAssemblies);
    }

    [Fact]
    public void Reads_duplicate_assembly_names_and_skipped_scripts()
    {
        var summary = UnityLogParser.Parse(DuplicateAssemblyLog, Project);

        var duplicates = summary.Diagnostics.Where(d => d.Kind == CompileDiagnosticKind.AssemblyDefinition).ToList();
        Assert.Equal(2, duplicates.Count);
        Assert.All(duplicates, d => Assert.Equal(IssueCodes.AssemblyDuplicate, d.Code));
        Assert.Equal(new[] { "Packages/com.test.bad/Runtime/Com.Test.Bad.asmdef", "Packages/com.test.dup/Runtime2/Dup2.asmdef" }, duplicates.Select(d => d.File));
        Assert.Equal("Com.Test.Bad", duplicates[0].Assembly);
        var skipped = Assert.Single(summary.Diagnostics, d => d.Code == IssueCodes.ScriptsOutsideAssembly);
        Assert.Equal(IssueSeverity.Info, skipped.Severity);
        Assert.Equal("Packages/com.test.bad/NoAsmdef/Loose.cs", skipped.File);
        Assert.True(summary.CompilerErrors);
        Assert.Equal(0, summary.Builds);
        Assert.True(summary.CompileRequested);
    }

    [Fact]
    public void Reads_a_clean_run_and_ignores_licensing_noise()
    {
        var summary = UnityLogParser.Parse(SuccessLog, Project);

        Assert.True(summary.LastBuildSucceeded);
        Assert.Equal(1, summary.Builds);
        Assert.False(summary.BuildStartedAfterLastResult);
        Assert.True(summary.ExitedCleanly);
        Assert.Equal(0, summary.ExitCode);
        Assert.False(summary.HasErrors);
        Assert.True(summary.LicenseResolved);
        Assert.False(summary.LicenseFailed);
        Assert.Equal(0, summary.ErrorCount);
        Assert.Single(summary.Diagnostics, d => d.Code == IssueCodes.ScriptsOutsideAssembly);
    }

    [Fact]
    public void Reads_paths_of_local_packages_outside_packages()
    {
        var summary = UnityLogParser.Parse(@".basisdev\com.test.local\Runtime\L.cs(1,64): error CS0029: Cannot implicitly convert type 'string' to 'int'", Project);

        var error = Assert.Single(summary.Diagnostics);
        Assert.Equal(".basisdev/com.test.local/Runtime/L.cs", error.File);
        Assert.Equal("CS0029", error.Code);
    }

    [Fact]
    public void Makes_absolute_paths_inside_the_project_relative()
    {
        var project = Path.Combine(Path.GetTempPath(), "basispm-parser", "Proj");
        var file = Path.Combine(project, "Library", "PackageCache", "com.example.tool@1a2b3c4d", "Runtime", "Tool.cs");
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere", "Pkg", "A.cs");
        var summary = UnityLogParser.Parse($"{file}(3,1): warning CS0618: 'Old' is obsolete\n{outside}(1,1): error CS1002: ; expected", project);

        Assert.Equal("Library/PackageCache/com.example.tool@1a2b3c4d/Runtime/Tool.cs", summary.Diagnostics[0].File);
        Assert.Equal(IssueSeverity.Warning, summary.Diagnostics[0].Severity);
        Assert.Equal(outside.Replace('\\', '/'), summary.Diagnostics[1].File);
    }

    [Fact]
    public void Gives_the_same_result_when_fed_one_character_at_a_time()
    {
        var parser = new UnityLogParser(Project);
        foreach (var c in CompileErrorLog) parser.Feed(c.ToString());
        parser.Complete();
        var whole = UnityLogParser.Parse(CompileErrorLog, Project);

        Assert.Equal(whole.Diagnostics, parser.Summary.Diagnostics);
        Assert.Equal(whole.FailedAssemblies, parser.Summary.FailedAssemblies);
        Assert.Equal(whole.Builds, parser.Summary.Builds);
    }

    [Fact]
    public void Detects_that_another_unity_has_the_project_open()
    {
        var summary = UnityLogParser.Parse(ProjectOpenOutput, Project);

        Assert.True(summary.ProjectAlreadyOpen);
        Assert.True(summary.BatchAborted);
        Assert.Equal("It looks like another Unity instance is running with this project open.", summary.FatalError);
    }

    [Fact]
    public void Flags_a_missing_license_only_when_none_resolved()
    {
        Assert.True(UnityLogParser.Parse("[Licensing::Module] Error: No valid Unity Editor license found. Please activate your license.").LicenseFailed);
        Assert.False(UnityLogParser.Parse("[Licensing::Module] Error: No valid Unity Editor license found.\n[Licensing::Client] Successfully resolved entitlement details").LicenseFailed);
    }

    [Fact]
    public void Reads_a_duplicate_dll_warning_that_runs_into_the_next_line()
    {
        const string line = "Duplicate assembly 'Newtonsoft.Json.dll' with different versions detected, using 'Packages/com.unity.nuget.newtonsoft-json/Runtime/Newtonsoft.Json.dll, AssemblyName=Newtonsoft.Json, Version=13.0.0.0, Culture=neutral, PublicKeyToken=30ad4fe6b2a6aeed' and ignoring 'Packages/org.basisvr.newtonsoft.json/Newtonsoft.Json.dll, AssemblyName=Newtonsoft.Json, Version=11.0.0.0, Culture=neutral, PublicKeyToken=null'.Begin MonoManager ReloadAssembly";
        var summary = UnityLogParser.Parse(line, Project);

        var warning = Assert.Single(summary.Diagnostics);
        Assert.Equal(IssueCodes.DllDuplicate, warning.Code);
        Assert.Equal(IssueSeverity.Warning, warning.Severity);
        Assert.Equal("Packages/org.basisvr.newtonsoft.json/Newtonsoft.Json.dll", warning.File);
        Assert.Contains("Packages/com.unity.nuget.newtonsoft-json/Runtime/Newtonsoft.Json.dll", warning.Message);
    }

    [Fact]
    public void Records_files_the_api_updater_rewrote()
    {
        const string log = """
            *** Tundra build failed (33.05 seconds), 1341 items updated, 2753 evaluated
            [API Updater] Updated Files:
            .basisdev/com.s-ilent.crosstone/Editor/BulkEditor/SCSS_BulkEditorContext.cs
            .basisdev/com.s-ilent.crosstone/Editor/BulkEditor/SCSS_BulkEditorWindow.cs
            .basisdev/com.s-ilent.crosstone/Editor/SCSS_ShaderBaking.cs
            Finished running ScriptUpdaters
            Starting: C:\Program Files\Unity\Hub\Editor\6000.7.0b4\Editor\Data\Tools\BuildPipeline\bee_backend.exe --ipc ScriptAssemblies
            """;
        var summary = UnityLogParser.Parse(log, Project);

        Assert.Equal(3, summary.ApiUpdatedFiles.Count);
        Assert.Equal(".basisdev/com.s-ilent.crosstone/Editor/SCSS_ShaderBaking.cs", summary.ApiUpdatedFiles[2]);
        Assert.Equal(3, summary.Diagnostics.Count(d => d.Code == UnityLogParser.ApiUpdatedCode && d.Severity == IssueSeverity.Info));
        Assert.True(summary.BuildStartedAfterLastResult);
    }

    [Fact]
    public void Keeps_spaces_in_assembly_names()
    {
        var summary = UnityLogParser.Parse("*** Tundra build failed (0.45 seconds), 2 items updated, 2753 evaluated\n## Script Compilation Error for: Csc Library/Bee/artifacts/1900b0aE.dag/Basis Framework.dll (+2 others)\n## Output:\nPackages\\com.basis.framework\\A.cs(1,2): error CS1513: } expected", Project);

        Assert.Equal(new[] { "Basis Framework" }, summary.FailedAssemblies);
        Assert.Equal("Basis Framework", Assert.Single(summary.Diagnostics).Assembly);
    }

    [Fact]
    public void Reads_compiler_errors_that_have_no_file()
    {
        var summary = UnityLogParser.Parse("CSC : error CS2001: Source file 'Missing.cs' could not be found.", Project);

        var error = Assert.Single(summary.Diagnostics);
        Assert.Equal("CS2001", error.Code);
        Assert.Null(error.File);
    }

    [Theory]
    [InlineData("[Package Manager] Done resolving packages in 0.10 seconds", "Resolving packages…")]
    [InlineData("Importing 'GUID: b4b3ffa9251461388209bfb4bb331a78 - Path: Packages/com.test.dup' with importer (DefaultImporter)", "Importing assets…")]
    [InlineData("[ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed", "Compiling scripts…")]
    [InlineData("*** Tundra build success (0.64 seconds), 25 items updated, 256 evaluated", "Scripts compiled.")]
    [InlineData("*** Tundra build failed (0.39 seconds), 22 items updated, 246 evaluated", "Scripts failed to compile.")]
    [InlineData("Reloading assemblies after forced synchronous recompile.", "Loading the compiled scripts…")]
    [InlineData("Exiting batchmode successfully now!", "Unity finished.")]
    [InlineData("[Licensing::Module] Licensing Background thread has ended after 0.06s", null)]
    public void Describes_progress_lines(string line, string? expected) => Assert.Equal(expected, UnityLogParser.DescribeProgress(line));

    [Fact]
    public void Reads_failed_assemblies_and_diagnostics_from_the_tundra_log()
    {
        using var temp = new TempDir("tundra");
        var path = temp.WriteFile("tundra.log.json", string.Join('\n',
            """{"msg":"init","dagFile":"Library/Bee/1900b0aE.dag","targets":["ScriptAssemblies"]}""",
            """{"msg":"noderesult","processed_node_count":1819,"number_of_nodes_ever_queued":2753,"annotation":"ILPP-Configuration Library/ilpp-configuration.nevergeneratedoutput","index":1757,"exitcode":0,"outputfile":"Library\\ilpp-configuration.nevergeneratedoutput"}""",
            """{"msg":"noderesult","annotation":"Csc Library/Bee/artifacts/1900b0aE.dag/Basis.OpenLipSync.Runtime.dll (+2 others)","index":12,"exitcode":0}""",
            """{"msg":"noderesult","processed_node_count":2748,"number_of_nodes_ever_queued":2753,"annotation":"Csc Library/Bee/artifacts/1900b0aE.dag/com.s-ilent.crosstone.Editor.dll (+2 others)","index":424,"exitcode":1,"outputfile":"Library\\Bee\\artifacts\\1900b0aE.dag\\com.s-ilent.crosstone.Editor.dll","stdout":".basisdev\\com.s-ilent.crosstone\\Editor\\SCSS_Inspector.cs(98,28): warning CS0114: 'Inspector.ValidateMaterial(Material)' hides inherited member 'ShaderGUI.ValidateMaterial(Material)'.\r\n.basisdev\\com.s-ilent.crosstone\\Editor\\SCSS_XSGradientImporter.cs(84,9): error CS0619: 'ProjectWindowUtil.CreateAssetWithContent(string, string, Texture2D, Action<int>)' is obsolete\r\n"}""",
            """{"msg":"noderesult","annotation":"ILPostProcess Library/Bee/artifacts/1900b0aE.dag/post-processed/Basis Framework.dll (+pdb)","index":30,"exitcode":3}""",
            "not json at all",
            """{"msg":"runNodeAction","displayName":"Compiling C# (Basis.OpenLipSync.Runtime)"}"""));

        var summary = UnityLogParser.ParseTundraLog(path, temp.Path);

        Assert.True(summary.Found);
        Assert.True(summary.Failed);
        Assert.Equal(new[] { "com.s-ilent.crosstone.Editor", "Basis Framework" }, summary.FailedAssemblies);
        Assert.Equal(2, summary.Nodes.Count);
        var error = Assert.Single(summary.Diagnostics, d => d.Code == "CS0619");
        Assert.Equal(".basisdev/com.s-ilent.crosstone/Editor/SCSS_XSGradientImporter.cs", error.File);
        Assert.Equal("com.s-ilent.crosstone.Editor", error.Assembly);
        Assert.Single(summary.Diagnostics, d => d.Code == "CS0114" && d.Severity == IssueSeverity.Warning);
        var step = Assert.Single(summary.Diagnostics, d => d.Kind == CompileDiagnosticKind.Other);
        Assert.Equal("Basis Framework", step.Assembly);
        Assert.Contains("ILPostProcess", step.Message);
    }

    [Fact]
    public void A_missing_tundra_log_is_reported_as_not_found()
    {
        var summary = UnityLogParser.ParseTundraLog(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.False(summary.Found);
        Assert.False(summary.Failed);
    }
}
