using BasisPM.Core.Models;
using BasisPM.Core.Services;
using BasisPM.Core.Tests.TestSupport;
using Xunit;

namespace BasisPM.Core.Tests;

public sealed class UnityCompileCheckServiceTests : IDisposable
{
    private const string Version = "6000.7.0b4";
    private readonly TempDir _temp = new("compilecheck");
    private readonly string _project;
    private readonly string _editor;

    public UnityCompileCheckServiceTests()
    {
        _project = _temp.CreateDir("Probe");
        _temp.CreateDir("Probe/Assets");
        _temp.WriteFile("Probe/ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {Version}\nm_EditorVersionWithRevision: {Version} (9debfb8362db)\n");
        _temp.WriteFile("Probe/Packages/manifest.json", """{ "dependencies": { "com.test.local": "file:../.basisdev/com.test.local", "com.unity.ugui": "2.0.0" } }""");
        _temp.WriteFile("Probe/Packages/com.test.bad/package.json", """{ "name": "com.test.bad", "version": "1.0.0" }""");
        _temp.WriteFile("Probe/.basisdev/com.test.local/package.json", """{ "name": "com.test.local", "version": "1.0.0" }""");
        _editor = _temp.WriteFile($"Editors/{Version}/Editor/Unity.exe", "");
    }

    public void Dispose() => _temp.Dispose();

    private IReadOnlyList<InstalledEditor> Editors => new[] { new InstalledEditor { Version = Version, Path = _editor } };

    private CompileCheckRequest Request(CompileCheckMode mode = CompileCheckMode.Auto, TimeSpan? timeout = null, TimeSpan? wait = null) => new()
    {
        UnityProjectPath = _project,
        Editors = Editors,
        Mode = mode,
        Timeout = timeout ?? TimeSpan.FromMinutes(1),
        OpenEditorWait = wait ?? TimeSpan.FromSeconds(10),
    };

    private static UnityCompileCheckService Service(FakeUnity runner, bool open = false) =>
        new(runner, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(150), _ => open);

    [Fact]
    public async Task Passes_when_unity_compiles_and_exits_cleanly()
    {
        var unity = new FakeUnity(0, "[ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed\n*** Tundra build success (0.64 seconds), 25 items updated, 256 evaluated\nExiting batchmode successfully now!\nApplication will exit with return code 0\n");
        var progress = new List<string>();

        var result = await Service(unity).RunAsync(Request(), progress.Add);

        Assert.Equal(CompileCheckStatus.Passed, result.Status);
        Assert.Equal(CompileCheckMode.Batch, result.Mode);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Version, result.UnityVersion);
        Assert.Equal(_editor, result.EditorPath);
        Assert.Equal(Path.Combine(_project, "Logs", UnityCompileCheckService.LogFileName), result.LogPath);
        Assert.True(File.Exists(result.LogPath));
        Assert.Equal("The project compiles.", result.Detail);
        Assert.Equal(new[] { "-batchmode", "-nographics", "-quit", "-projectPath", _project, "-logFile", result.LogPath }, unity.Arguments);
        Assert.DoesNotContain(unity.Arguments!, a => a.Contains("apiupdate", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(progress, p => p.StartsWith($"Starting Unity {Version}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_with_the_errors_attributed_to_their_packages()
    {
        var unity = new FakeUnity(1, """
            [234/246    0s] Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others)
            ##### ExitCode
            1
            ##### Output
            Packages\com.test.bad\Runtime\Broken.cs(5,28): error CS0246: The type or namespace name 'NotAType' could not be found (are you missing a using directive or an assembly reference?)
            .basisdev\com.test.local\Runtime\L.cs(1,64): error CS0029: Cannot implicitly convert type 'string' to 'int'
            Assets\UserScript.cs(2,10): warning CS0168: The variable 'x' is declared but never used
            *** Tundra build failed (0.39 seconds), 22 items updated, 246 evaluated
            ## Script Compilation Error for: Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others)
            Scripts have compiler errors.
            Application will exit with return code 1
            """, "\nAborting batchmode due to failure:\nScripts have compiler errors.\n");

        var result = await Service(unity).RunAsync(Request());

        Assert.Equal(CompileCheckStatus.Failed, result.Status);
        Assert.Equal(2, result.ErrorCount);
        Assert.Equal(1, result.WarningCount);
        Assert.Equal("com.test.bad", result.Diagnostics.Single(d => d.Code == "CS0246").Package);
        Assert.Equal("com.test.local", result.Diagnostics.Single(d => d.Code == "CS0029").Package);
        Assert.Equal(ProjectPackagesAssets, result.Diagnostics.Single(d => d.Code == "CS0168").Package);
        Assert.Equal(new[] { "Com.Test.Bad" }, result.FailedAssemblies);
        Assert.Equal(IssueSeverity.Error, result.Diagnostics[0].Severity);
        Assert.Equal("Unity found 2 compile errors: 1 in com.test.bad, 1 in com.test.local.", result.Detail);
    }

    private static string ProjectPackagesAssets => UnityPackageMap.AssetsId;

    [Fact]
    public async Task Reports_package_resolution_failures()
    {
        var unity = new FakeUnity(1, """
            An error occurred while resolving packages:
              Package com.test.bad@file:C:\Probe\Packages\com.test.bad has invalid dependencies or related test packages:
                com.test.missing (dependency): Package [com.test.missing@1.2.3] cannot be found
            [ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1
            """);

        var result = await Service(unity).RunAsync(Request());

        Assert.Equal(CompileCheckStatus.PackagesFailed, result.Status);
        var problem = Assert.Single(result.Diagnostics);
        Assert.Equal("com.test.bad", problem.Package);
        Assert.StartsWith("Unity couldn't resolve the project's packages, so nothing was compiled. com.test.bad: com.test.missing (dependency)", result.Detail);
    }

    [Fact]
    public async Task Reports_a_crash_with_the_end_of_the_log()
    {
        var unity = new FakeUnity(3, "Loading something\nNative crash in UnityPlayer.dll\n");

        var result = await Service(unity).RunAsync(Request());

        Assert.Equal(CompileCheckStatus.Crashed, result.Status);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("exited with code 3", result.Detail);
        Assert.Contains("Native crash in UnityPlayer.dll", result.Detail);
    }

    [Fact]
    public async Task Stops_unity_when_it_runs_past_the_timeout()
    {
        var unity = new FakeUnity(0, "[Package Manager] Done resolving packages in 0.10 seconds\n") { Hang = true };

        var result = await Service(unity).RunAsync(Request(timeout: TimeSpan.FromMilliseconds(300)));

        Assert.Equal(CompileCheckStatus.TimedOut, result.Status);
        Assert.True(unity.SawCancellation);
        Assert.Contains("didn't finish within 1 second.", result.Detail);
    }

    [Fact]
    public async Task Stops_unity_when_the_check_is_cancelled()
    {
        var unity = new FakeUnity(0, "") { Hang = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var result = await Service(unity).RunAsync(Request(), null, cts.Token);

        Assert.Equal(CompileCheckStatus.Cancelled, result.Status);
        Assert.True(unity.SawCancellation);
    }

    [Fact]
    public async Task Recognises_another_unity_holding_the_project()
    {
        var unity = new FakeUnity(1, "Successfully changed project path to: C:\\Probe\n[ExitDontLaunchBugReporter] Exiting without the bug reporter. Application will exit with return code 1\n",
            "\nAborting batchmode due to fatal error:\nIt looks like another Unity instance is running with this project open.\n\nMultiple Unity instances cannot open the same project.\n");

        var result = await Service(unity).RunAsync(Request());

        Assert.Equal(CompileCheckStatus.ProjectOpen, result.Status);
    }

    [Fact]
    public async Task Reports_a_missing_editor_without_starting_anything()
    {
        var unity = new FakeUnity(0, "");
        var request = new CompileCheckRequest { UnityProjectPath = _project, Editors = new[] { new InstalledEditor { Version = "6000.5.10f1", Path = _editor } } };

        var result = await Service(unity).RunAsync(request);

        Assert.Equal(CompileCheckStatus.EditorMissing, result.Status);
        Assert.Contains($"Unity {Version} isn't installed", result.Detail);
        Assert.Null(unity.Arguments);
    }

    [Fact]
    public async Task Batch_mode_refuses_a_project_unity_has_open()
    {
        var unity = new FakeUnity(0, "");

        var result = await Service(unity, open: true).RunAsync(Request(CompileCheckMode.Batch));

        Assert.Equal(CompileCheckStatus.ProjectOpen, result.Status);
        Assert.Null(unity.Arguments);
    }

    [Fact]
    public async Task Open_editor_mode_needs_unity_to_have_the_project_open()
    {
        var result = await Service(new FakeUnity(0, "")).RunAsync(Request(CompileCheckMode.OpenEditor));

        Assert.Equal(CompileCheckStatus.Waiting, result.Status);
    }

    [Fact]
    public void Finds_the_editor_for_the_exact_version()
    {
        var missing = new InstalledEditor { Version = Version, Path = Path.Combine(_temp.Path, "gone", "Unity.exe") };
        var other = new InstalledEditor { Version = "6000.7.0b3", Path = _editor };
        var match = new InstalledEditor { Version = "6000.7.0B4", Path = _editor };

        Assert.Same(match, UnityCompileCheckService.FindEditor(new[] { missing, other, match }, Version));
        Assert.Null(UnityCompileCheckService.FindEditor(new[] { missing, other }, Version));
        Assert.Null(UnityCompileCheckService.FindEditor(new[] { match }, null));
    }

    [Fact]
    public void Uses_the_binary_inside_a_mac_app_bundle()
    {
        var bundle = _temp.CreateDir("Mac/Unity.app");
        var inner = _temp.WriteFile("Mac/Unity.app/Contents/MacOS/Unity", "");

        Assert.Equal(inner, UnityCompileCheckService.EditorExecutable(bundle));
        Assert.Equal(_editor, UnityCompileCheckService.EditorExecutable(_editor));
        Assert.Null(UnityCompileCheckService.EditorExecutable(_temp.CreateDir("Mac/Empty.app")));
    }

    [Fact]
    public void Reads_the_project_log_first_and_a_global_log_only_when_it_names_the_project()
    {
        var global = _temp.WriteFile("Global/Editor.log", $"COMMAND LINE ARGUMENTS:\nUnity.exe\n-projectpath\n{_project.Replace('\\', '/').ToUpperInvariant()}\n");
        var other = _temp.WriteFile("Global/Other.log", "COMMAND LINE ARGUMENTS:\nUnity.exe\n-projectpath\nC:/Somewhere/Else\n");
        var moved = _temp.WriteFile("Global/Moved.log", $"-projectpath\n{_project}\nLogs moved to project-relative Editor.log file, and subsequent logs will only be written to this file.\n");

        Assert.Equal(global, UnityCompileCheckService.EditorLogPath(_project, global));
        Assert.Null(UnityCompileCheckService.EditorLogPath(_project, other));
        Assert.Null(UnityCompileCheckService.EditorLogPath(_project, moved));
        var local = _temp.WriteFile("Probe/Logs/Editor.log", "");
        Assert.Equal(local, UnityCompileCheckService.EditorLogPath(_project, other));
    }

    [Fact]
    public async Task Reads_the_open_editors_next_compile_and_merges_the_tundra_log()
    {
        var log = _temp.WriteFile("Probe/Logs/Editor.log", "*** Tundra build failed (1.00 seconds), 1 items updated, 2 evaluated\nAssets\\Old.cs(1,1): error CS1002: ; expected\n");
        var tundra = _temp.WriteFile("Probe/Library/Bee/tundra.log.json", """{"msg":"init"}""");
        File.SetLastWriteTimeUtc(tundra, DateTime.UtcNow.AddMinutes(-5));
        var progress = new List<string>();
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(Request(), progress.Add);

        await Task.Delay(80);
        File.AppendAllText(log, "[ScriptCompilation] Requested script compilation because: AssetDatabase observed changes in script compilation related files\n");
        await Task.Delay(80);
        File.AppendAllText(log, ".basisdev\\com.test.local\\Runtime\\L.cs(1,64): error CS0029: Cannot implicitly convert type 'string' to 'int'\n*** Tundra build failed (0.31 seconds), 12 items updated, 266 evaluated\n");
        File.WriteAllText(tundra, """{"msg":"noderesult","annotation":"Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Local.dll (+2 others)","exitcode":1,"stdout":".basisdev\\com.test.local\\Runtime\\L.cs(1,64): error CS0029: Cannot implicitly convert type 'string' to 'int'\r\n"}""" + "\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.Failed, result.Status);
        Assert.Equal(CompileCheckMode.OpenEditor, result.Mode);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("CS0029", error.Code);
        Assert.Equal("com.test.local", error.Package);
        Assert.Equal("Com.Test.Local", error.Assembly);
        Assert.Equal(new[] { "Com.Test.Local" }, result.FailedAssemblies);
        Assert.Equal(log, result.LogPath);
        Assert.Contains(progress, p => p.StartsWith("Unity has this project open", StringComparison.Ordinal));
        Assert.Contains("Compiling scripts…", progress);
    }

    [Fact]
    public async Task Falls_back_to_the_tundra_log_when_there_is_no_editor_log()
    {
        var tundra = _temp.WriteFile("Probe/Library/Bee/tundra.log.json", """{"msg":"init"}""");
        File.SetLastWriteTimeUtc(tundra, DateTime.UtcNow.AddMinutes(-5));
        var request = new CompileCheckRequest { UnityProjectPath = _project, EditorLog = Path.Combine(_temp.Path, "no-such.log"), OpenEditorWait = TimeSpan.FromSeconds(10) };
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(request);

        await Task.Delay(60);
        File.WriteAllText(tundra, """{"msg":"noderesult","annotation":"Csc Library/Bee/artifacts/1900b0aE.dag/Com.Test.Bad.dll (+2 others)","exitcode":1,"stdout":"Packages\\com.test.bad\\Runtime\\Broken.cs(5,28): error CS0246: The type or namespace name 'NotAType' could not be found\r\n"}""" + "\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.Failed, result.Status);
        Assert.Null(result.LogPath);
        var error = Assert.Single(result.Diagnostics);
        Assert.Equal("com.test.bad", error.Package);
        Assert.Equal("Com.Test.Bad", error.Assembly);
    }

    [Fact]
    public async Task Waits_for_the_compile_after_the_api_updater()
    {
        var log = _temp.WriteFile("Probe/Logs/Editor.log", "earlier\n");
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(Request());

        await Task.Delay(60);
        File.AppendAllText(log, "Assets\\A.cs(1,1): error CS0619: 'Old' is obsolete\n*** Tundra build failed (33.05 seconds), 1341 items updated, 2753 evaluated\n[API Updater] Updated Files:\nAssets/A.cs\nFinished running ScriptUpdaters\nStarting: C:\\Unity\\bee_backend.exe --ipc ScriptAssemblies\n");
        await Task.Delay(400);
        Assert.False(check.IsCompleted);
        File.AppendAllText(log, "*** Tundra build success (2.00 seconds), 3 items updated, 2753 evaluated\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.Passed, result.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == UnityLogParser.ApiUpdatedCode && d.File == "Assets/A.cs");
    }

    [Fact]
    public async Task Says_unity_has_not_noticed_the_change_when_nothing_happens()
    {
        _temp.WriteFile("Probe/Logs/Editor.log", "*** Tundra build success (1.00 seconds), 1 items updated, 2 evaluated\n");

        var result = await Service(new FakeUnity(0, ""), open: true).RunAsync(Request(wait: TimeSpan.FromMilliseconds(300)));

        Assert.Equal(CompileCheckStatus.Waiting, result.Status);
        Assert.Contains("Switch to the Unity window", result.Detail);
    }

    [Fact]
    public async Task Is_inconclusive_when_unity_starts_but_does_not_finish()
    {
        var log = _temp.WriteFile("Probe/Logs/Editor.log", "");
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(Request(wait: TimeSpan.FromMilliseconds(400)));

        await Task.Delay(60);
        File.AppendAllText(log, "[ScriptCompilation] Requested script compilation because: Assembly Definition File(s) changed\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.Inconclusive, result.Status);
    }

    [Fact]
    public async Task Reports_package_resolution_in_the_open_editor()
    {
        var log = _temp.WriteFile("Probe/Logs/Editor.log", "");
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(Request());

        await Task.Delay(60);
        File.AppendAllText(log, "An error occurred while resolving packages:\n  Project has invalid dependencies:\n    com.unity.nonexistent: Package [com.unity.nonexistent@1.0.0] cannot be found\n[Package Manager] Done resolving packages in 0.20 seconds\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.PackagesFailed, result.Status);
        Assert.Equal("com.unity.nonexistent", Assert.Single(result.Diagnostics).Package);
    }

    [Fact]
    public async Task Starts_over_when_unity_restarts_and_truncates_its_log()
    {
        var log = _temp.WriteFile("Probe/Logs/Editor.log", new string('x', 4000) + "\n");
        var check = Service(new FakeUnity(0, ""), open: true).RunAsync(Request());

        await Task.Delay(60);
        File.WriteAllText(log, "*** Tundra build success (5.00 seconds), 300 items updated, 2753 evaluated\n");
        var result = await check;

        Assert.Equal(CompileCheckStatus.Passed, result.Status);
    }

    private sealed class FakeUnity(int exitCode, string log, string output = "") : IUnityProcessRunner
    {
        public bool Hang { get; init; }
        public IReadOnlyList<string>? Arguments { get; private set; }
        public bool SawCancellation { get; private set; }

        public async Task<UnityProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? onOutput, CancellationToken ct)
        {
            Arguments = arguments;
            var logPath = arguments[arguments.ToList().IndexOf("-logFile") + 1];
            await File.WriteAllTextAsync(logPath, log, CancellationToken.None);
            if (Hang)
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                {
                    SawCancellation = true;
                    throw;
                }
            }
            return new UnityProcessResult(exitCode, output);
        }
    }
}
