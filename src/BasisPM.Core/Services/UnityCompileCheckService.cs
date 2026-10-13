using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class CompileCheckRequest
{
    public required string UnityProjectPath { get; init; }
    public string? UnityVersion { get; init; }
    public IReadOnlyList<InstalledEditor> Editors { get; init; } = Array.Empty<InstalledEditor>();
    public CompileCheckMode Mode { get; init; } = CompileCheckMode.Auto;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);
    public TimeSpan OpenEditorWait { get; init; } = TimeSpan.FromMinutes(10);
    public IReadOnlyList<ProjectPackageRef>? Packages { get; init; }
    public string? LogPath { get; init; }
    public string? EditorLog { get; init; }
}

public sealed record UnityProcessResult(int ExitCode, string Output);

public interface IUnityProcessRunner
{
    Task<UnityProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output, CancellationToken ct);
}

public sealed class UnityProcessRunner : IUnityProcessRunner
{
    public async Task<UnityProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var text = new StringBuilder();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLine(string? line)
        {
            if (line is null) return;
            lock (text) text.AppendLine(line);
            output?.Invoke(line);
        }
        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.Exited += (_, _) => exited.TrySetResult();
        if (!process.Start()) throw new InvalidOperationException($"Couldn't start {executable}.");
        using var stop = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { DiagnosticLog.Write("Stopping Unity after a cancelled compile check", ex); }
        });
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (process.HasExited) exited.TrySetResult();
        await exited.Task.WaitAsync(ct).ConfigureAwait(false);
        using (var grace = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        lock (text) return new UnityProcessResult(process.ExitCode, text.ToString());
    }
}

public sealed class UnityCompileCheckService
{
    public const string LogFileName = "BasisPM-CompileCheck.log";
    public const int MaxNonErrors = 2000;
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(20);

    private readonly IUnityProcessRunner _runner;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _settle;
    private readonly Func<string, bool> _isOpenInUnity;

    public UnityCompileCheckService(IUnityProcessRunner? runner = null, TimeSpan? pollInterval = null, TimeSpan? settle = null, Func<string, bool>? isOpenInUnity = null)
    {
        _runner = runner ?? new UnityProcessRunner();
        _poll = pollInterval ?? TimeSpan.FromMilliseconds(500);
        _settle = settle ?? TimeSpan.FromSeconds(4);
        _isOpenInUnity = isOpenInUnity ?? UnityProjectService.IsOpenInUnity;
    }

    public static InstalledEditor? FindEditor(IEnumerable<InstalledEditor> editors, string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : editors.FirstOrDefault(e => string.Equals(e.Version, version.Trim(), StringComparison.OrdinalIgnoreCase) && EditorExecutable(e.Path) is not null);

    public static string? EditorExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (File.Exists(path)) return path;
        if (Directory.Exists(path))
        {
            var inner = Path.Combine(path, "Contents", "MacOS", "Unity");
            if (File.Exists(inner)) return inner;
        }
        return null;
    }

    public static IReadOnlyList<string> BatchArguments(string unityProjectPath, string logPath) =>
        new[] { "-batchmode", "-nographics", "-quit", "-projectPath", unityProjectPath, "-logFile", logPath };

    public static string DefaultLogPath(string unityProjectPath) => Path.Combine(unityProjectPath, "Logs", LogFileName);

    public static string TundraLogPath(string unityProjectPath) => Path.Combine(unityProjectPath, "Library", "Bee", "tundra.log.json");

    public static string? EditorLogPath(string unityProjectPath, string? globalLog = null)
    {
        var local = Path.Combine(unityProjectPath, "Logs", "Editor.log");
        if (File.Exists(local)) return local;
        var global = globalLog ?? GlobalEditorLog();
        return global is not null && File.Exists(global) && LogNamesProject(global, unityProjectPath) ? global : null;
    }

    private static string? GlobalEditorLog()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "Editor", "Editor.log");
        if (home.Length == 0) return null;
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? Path.Combine(home, "Library", "Logs", "Unity", "Editor.log") : Path.Combine(home, ".config", "unity3d", "Editor.log");
    }

    public static bool LogNamesProject(string logPath, string unityProjectPath)
    {
        var head = ReadStart(logPath, 256 * 1024);
        if (head.Contains("Logs moved to project-relative Editor.log", StringComparison.Ordinal)) return false;
        static string Normalize(string text) => text.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        return Normalize(head).Contains(Normalize(Path.GetFullPath(unityProjectPath)), StringComparison.Ordinal);
    }

    private static string ReadStart(string path, int bytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, bytes)];
            var read = stream.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public async Task<CompileCheckResult> RunAsync(CompileCheckRequest request, Action<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var project = Path.GetFullPath(request.UnityProjectPath);
        var version = string.IsNullOrWhiteSpace(request.UnityVersion) || request.UnityVersion == "unknown" ? UnityProjectService.ReadUnityVersion(project) : request.UnityVersion.Trim();
        if (!Directory.Exists(project) || !Directory.Exists(Path.Combine(project, "Assets")))
            return new CompileCheckResult { Status = CompileCheckStatus.Inconclusive, Mode = request.Mode, Detail = $"{project} isn't a Unity project.", UnityVersion = version, Started = started, Duration = clock.Elapsed };

        var open = _isOpenInUnity(project);
        var mode = request.Mode == CompileCheckMode.Auto ? (open ? CompileCheckMode.OpenEditor : CompileCheckMode.Batch) : request.Mode;
        if (mode == CompileCheckMode.OpenEditor)
        {
            if (!open)
                return new CompileCheckResult
                {
                    Status = CompileCheckStatus.Waiting, Mode = mode, UnityVersion = version, Started = started, Duration = clock.Elapsed,
                    Detail = "Unity doesn't have this project open, so there's no compile to wait for.",
                };
            return await WatchOpenEditorAsync(request, project, version, started, clock, progress, ct).ConfigureAwait(false);
        }
        if (open)
            return new CompileCheckResult
            {
                Status = CompileCheckStatus.ProjectOpen, Mode = mode, UnityVersion = version, Started = started, Duration = clock.Elapsed,
                Detail = "Unity has this project open, so it can't be checked in the background. Close Unity and try again, or let BasisPM read the open editor's result.",
            };

        var editor = FindEditor(request.Editors, version);
        var executable = EditorExecutable(editor?.Path);
        if (executable is null)
            return new CompileCheckResult
            {
                Status = CompileCheckStatus.EditorMissing, Mode = mode, UnityVersion = version, Started = started, Duration = clock.Elapsed,
                Detail = $"Unity {version} isn't installed, so the project can't be compiled. Install it from the Unity tab, then try again.",
            };
        return await RunBatchAsync(request, project, version, executable, started, clock, progress, ct).ConfigureAwait(false);
    }

    private async Task<CompileCheckResult> RunBatchAsync(CompileCheckRequest request, string project, string version, string executable, DateTimeOffset started, Stopwatch clock, Action<string>? progress, CancellationToken ct)
    {
        var logPath = PrepareLog(request.LogPath ?? DefaultLogPath(project));
        var tundraPath = TundraLogPath(project);
        var tundraBefore = File.Exists(tundraPath) ? File.GetLastWriteTimeUtc(tundraPath) : (DateTime?)null;
        progress?.Invoke($"Starting Unity {version} to compile the project…");

        using var timeout = new CancellationTokenSource(request.Timeout > MaxTimeout ? MaxTimeout : request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var stopTail = new CancellationTokenSource();
        var tail = FollowAsync(new LogTail(logPath, 0), progress, stopTail.Token);
        UnityProcessResult? run = null;
        CompileCheckStatus? stopped = null;
        string? failure = null;
        try { run = await _runner.RunAsync(executable, BatchArguments(project, logPath), null, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { stopped = ct.IsCancellationRequested ? CompileCheckStatus.Cancelled : CompileCheckStatus.TimedOut; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Running Unity for a compile check of {project}", ex);
            failure = ex.Message;
        }
        finally
        {
            stopTail.Cancel();
            try { await tail.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        var parser = new UnityLogParser(project);
        parser.FeedFile(logPath);
        parser.Feed("\n");
        if (run is not null) parser.Feed(run.Output);
        parser.Complete();
        var summary = parser.Summary;
        var tundra = File.Exists(tundraPath) && File.GetLastWriteTimeUtc(tundraPath) != tundraBefore ? UnityLogParser.ParseTundraLog(tundraPath, project, CancellationToken.None) : null;
        var diagnostics = Finish(Merge(summary.Diagnostics, tundra?.Diagnostics), request.Packages ?? UnityPackageMap.List(project), project);
        var failed = summary.FailedAssemblies.Union(tundra?.FailedAssemblies ?? Enumerable.Empty<string>(), StringComparer.Ordinal).ToList();

        CompileCheckStatus status;
        string detail;
        if (failure is not null)
        {
            status = CompileCheckStatus.Crashed;
            detail = $"Unity couldn't be started: {failure}";
        }
        else if (stopped == CompileCheckStatus.Cancelled)
        {
            status = CompileCheckStatus.Cancelled;
            detail = "The compile check was cancelled.";
        }
        else if (stopped == CompileCheckStatus.TimedOut)
        {
            status = CompileCheckStatus.TimedOut;
            detail = $"Unity didn't finish within {Minutes(request.Timeout)}. The log is at {logPath}.";
        }
        else if (summary.ProjectAlreadyOpen)
        {
            status = CompileCheckStatus.ProjectOpen;
            detail = "Another Unity window has this project open. Close it and try again.";
        }
        else if (summary.LicenseFailed)
        {
            status = CompileCheckStatus.NoLicense;
            detail = "Unity couldn't find a license. Sign in to Unity Hub, then try again.";
        }
        else if (summary.PackageResolutionFailed || diagnostics.Any(d => d.Kind == CompileDiagnosticKind.PackageResolution && d.Severity == IssueSeverity.Error))
        {
            status = CompileCheckStatus.PackagesFailed;
            detail = DescribePackages(diagnostics);
        }
        else if (summary.CompilerErrors || summary.LastBuildSucceeded == false || diagnostics.Any(IsBlockingError))
        {
            status = CompileCheckStatus.Failed;
            detail = DescribeErrors(diagnostics);
        }
        else if (run!.ExitCode == 0)
        {
            status = CompileCheckStatus.Passed;
            detail = DescribePassed(diagnostics, "The project compiles.");
        }
        else
        {
            status = CompileCheckStatus.Crashed;
            var tailText = UnityLogParser.Tail(ReadEnd(logPath, 16 * 1024), 8);
            var lead = summary.LastBuildSucceeded == true ? $"The scripts compiled, but Unity then exited with code {run.ExitCode}." : $"Unity exited with code {run.ExitCode} before it finished.";
            detail = tailText.Length == 0 ? lead : $"{lead} Last lines of the log:\n{tailText}";
        }

        return new CompileCheckResult
        {
            Status = status,
            Mode = CompileCheckMode.Batch,
            Detail = detail,
            Diagnostics = diagnostics,
            FailedAssemblies = failed,
            LogPath = logPath,
            UnityVersion = version,
            EditorPath = executable,
            ExitCode = run?.ExitCode ?? summary.ExitCode,
            Started = started,
            Duration = clock.Elapsed,
        };
    }

    private async Task<CompileCheckResult> WatchOpenEditorAsync(CompileCheckRequest request, string project, string version, DateTimeOffset started, Stopwatch clock, Action<string>? progress, CancellationToken ct)
    {
        var logPath = request.EditorLog is { } explicitLog ? (File.Exists(explicitLog) ? explicitLog : null) : EditorLogPath(project);
        var tundraPath = TundraLogPath(project);
        var tundraBefore = File.Exists(tundraPath) ? File.GetLastWriteTimeUtc(tundraPath) : (DateTime?)null;
        var tail = new LogTail(logPath, logPath is not null && File.Exists(logPath) ? new FileInfo(logPath).Length : 0);
        var parser = new UnityLogParser(project);
        var lines = new LineSplitter();
        string? lastProgress = null;
        progress?.Invoke("Unity has this project open. Waiting for it to compile the change; switch to the Unity window if nothing happens.");
        var lastActivity = clock.Elapsed;
        TimeSpan? resultSeen = null, tundraSeen = null;
        DateTime? tundraStamp = tundraBefore;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var text = tail.ReadNew(out var reset);
                if (reset)
                {
                    parser = new UnityLogParser(project);
                    lines = new LineSplitter();
                }
                if (text.Length > 0)
                {
                    lastActivity = clock.Elapsed;
                    parser.Feed(text);
                    foreach (var line in lines.Push(text))
                        if (UnityLogParser.DescribeProgress(line) is { } message && message != lastProgress)
                        {
                            lastProgress = message;
                            progress?.Invoke(message);
                        }
                }
                var summary = parser.Summary;
                var now = clock.Elapsed;
                if (summary.Builds > 0 && !summary.BuildStartedAfterLastResult)
                {
                    resultSeen ??= now;
                    if (now - resultSeen.Value >= _settle) break;
                }
                else resultSeen = null;
                if ((summary.PackageResolutionFailed || summary.Diagnostics.Any(d => d.Kind == CompileDiagnosticKind.AssemblyDefinition && d.Severity == IssueSeverity.Error))
                    && summary.Builds == 0 && now - lastActivity >= _settle) break;
                if (logPath is null && File.Exists(tundraPath))
                {
                    var stamp = File.GetLastWriteTimeUtc(tundraPath);
                    if (stamp != tundraStamp)
                    {
                        tundraStamp = stamp;
                        tundraSeen = now;
                    }
                    else if (tundraSeen is not null && now - tundraSeen.Value >= _settle) break;
                }
                if (now >= request.OpenEditorWait)
                {
                    var waiting = summary.Builds == 0 && !summary.CompileRequested && !summary.PackagesResolving && tundraSeen is null;
                    return new CompileCheckResult
                    {
                        Status = waiting ? CompileCheckStatus.Waiting : CompileCheckStatus.Inconclusive,
                        Mode = CompileCheckMode.OpenEditor,
                        Detail = waiting
                            ? "Unity hasn't picked up the change yet. Switch to the Unity window so it reloads the project, then run the check again."
                            : $"Unity started on the change but didn't finish compiling within {Minutes(request.OpenEditorWait)}. Run the check again once Unity is idle.",
                        LogPath = logPath,
                        UnityVersion = version,
                        Started = started,
                        Duration = clock.Elapsed,
                    };
                }
                await Task.Delay(_poll, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new CompileCheckResult { Status = CompileCheckStatus.Cancelled, Mode = CompileCheckMode.OpenEditor, Detail = "The compile check was cancelled.", LogPath = logPath, UnityVersion = version, Started = started, Duration = clock.Elapsed };
        }

        parser.Complete();
        var final = parser.Summary;
        var tundra = File.Exists(tundraPath) && File.GetLastWriteTimeUtc(tundraPath) != tundraBefore ? UnityLogParser.ParseTundraLog(tundraPath, project, CancellationToken.None) : null;
        var diagnostics = Finish(Merge(final.Diagnostics, tundra?.Diagnostics), request.Packages ?? UnityPackageMap.List(project), project);
        var failed = final.FailedAssemblies.Union(tundra?.FailedAssemblies ?? Enumerable.Empty<string>(), StringComparer.Ordinal).ToList();
        CompileCheckStatus status;
        string detail;
        if (final.PackageResolutionFailed || diagnostics.Any(d => d.Kind == CompileDiagnosticKind.PackageResolution && d.Severity == IssueSeverity.Error))
        {
            status = CompileCheckStatus.PackagesFailed;
            detail = DescribePackages(diagnostics);
        }
        else if (final.LastBuildSucceeded == false || final.CompilerErrors || (tundra?.Failed == true && final.Builds == 0) || diagnostics.Any(IsBlockingError))
        {
            status = CompileCheckStatus.Failed;
            detail = DescribeErrors(diagnostics);
        }
        else
        {
            status = CompileCheckStatus.Passed;
            detail = DescribePassed(diagnostics, "Unity compiled the project without errors.");
        }
        return new CompileCheckResult
        {
            Status = status,
            Mode = CompileCheckMode.OpenEditor,
            Detail = detail,
            Diagnostics = diagnostics,
            FailedAssemblies = failed,
            LogPath = logPath,
            UnityVersion = version,
            Started = started,
            Duration = clock.Elapsed,
        };
    }

    private static async Task FollowAsync(LogTail tail, Action<string>? progress, CancellationToken ct)
    {
        var lines = new LineSplitter();
        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            var text = tail.ReadNew(out var reset);
            if (reset) lines = new LineSplitter();
            foreach (var line in lines.Push(text))
                if (UnityLogParser.DescribeProgress(line) is { } message && message != last)
                {
                    last = message;
                    progress?.Invoke(message);
                }
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static bool IsBlockingError(CompileDiagnostic diagnostic) =>
        diagnostic.Severity == IssueSeverity.Error && diagnostic.Kind is CompileDiagnosticKind.Compiler or CompileDiagnosticKind.AssemblyDefinition;

    private static List<CompileDiagnostic> Merge(IEnumerable<CompileDiagnostic> log, IEnumerable<CompileDiagnostic>? tundra)
    {
        var merged = new List<CompileDiagnostic>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var diagnostic in log.Concat(tundra ?? Enumerable.Empty<CompileDiagnostic>()))
        {
            var key = string.Join('|', diagnostic.Kind, diagnostic.Severity, diagnostic.File, diagnostic.Line, diagnostic.Column, diagnostic.Code, diagnostic.Message, diagnostic.Package);
            if (index.TryGetValue(key, out var at))
            {
                if (merged[at].Assembly is null && diagnostic.Assembly is not null) merged[at] = merged[at] with { Assembly = diagnostic.Assembly };
                continue;
            }
            index[key] = merged.Count;
            merged.Add(diagnostic);
        }
        return merged;
    }

    private static List<CompileDiagnostic> Finish(IEnumerable<CompileDiagnostic> diagnostics, IReadOnlyList<ProjectPackageRef> packages, string project)
    {
        var attributed = diagnostics.Select(d => d.Package is not null || d.File is null ? d : UnityPackageMap.Owner(packages, project, d.File) is { } owner ? d with { Package = owner.Id } : d)
            .OrderBy(d => d.Severity switch { IssueSeverity.Error => 0, IssueSeverity.Warning => 1, _ => 2 })
            .ThenBy(d => d.Package ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.File ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Line ?? 0)
            .ToList();
        var errors = attributed.Where(d => d.Severity == IssueSeverity.Error);
        return errors.Concat(attributed.Where(d => d.Severity != IssueSeverity.Error).Take(MaxNonErrors)).ToList();
    }

    public static string DescribeErrors(IReadOnlyCollection<CompileDiagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == IssueSeverity.Error).ToList();
        if (errors.Count == 0) return "Unity reported that the scripts failed to compile.";
        var where = errors.GroupBy(d => d.Package ?? "the project", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} in {g.Key}").ToList();
        var noun = errors.Count == 1 ? "compile error" : "compile errors";
        return where.Count == 1
            ? $"Unity found {errors.Count} {noun} in {errors[0].Package ?? "the project"}."
            : $"Unity found {errors.Count} {noun}: {string.Join(", ", where)}.";
    }

    public static string DescribePackages(IReadOnlyCollection<CompileDiagnostic> diagnostics)
    {
        var problems = diagnostics.Where(d => d.Kind == CompileDiagnosticKind.PackageResolution).ToList();
        if (problems.Count == 0) return "Unity couldn't resolve the project's packages.";
        var first = problems[0];
        var more = problems.Count > 1 ? $" (and {problems.Count - 1} more)" : "";
        return $"Unity couldn't resolve the project's packages, so nothing was compiled. {first.Package}: {first.Message}{more}";
    }

    private static string DescribePassed(IReadOnlyCollection<CompileDiagnostic> diagnostics, string lead)
    {
        var warnings = diagnostics.Count(d => d.Severity == IssueSeverity.Warning);
        return warnings == 0 ? lead : warnings == 1 ? $"{lead} Unity reported 1 warning." : $"{lead} Unity reported {warnings} warnings.";
    }

    private static string Minutes(TimeSpan span)
    {
        var (count, unit) = span.TotalMinutes >= 1 ? ((int)Math.Round(span.TotalMinutes), "minute") : (Math.Max(1, (int)Math.Round(span.TotalSeconds)), "second");
        return $"{count.ToString(CultureInfo.InvariantCulture)} {unit}{(count == 1 ? "" : "s")}";
    }

    private static string PrepareLog(string logPath)
    {
        var folder = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        try
        {
            if (File.Exists(logPath)) File.Delete(logPath);
            return logPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Replacing the compile check log {logPath}", ex);
            return Path.Combine(folder ?? "", $"{Path.GetFileNameWithoutExtension(logPath)}-{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(logPath)}");
        }
    }

    private static string ReadEnd(string path, int bytes)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > bytes) stream.Seek(-bytes, SeekOrigin.End);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"Reading {path}", ex);
            return "";
        }
    }

    private sealed class LogTail(string? path, long offset)
    {
        private const int MaxChunk = 8 * 1024 * 1024;
        private long _offset = offset;
        private Decoder _decoder = Encoding.UTF8.GetDecoder();

        public string ReadNew(out bool reset)
        {
            reset = false;
            if (path is null || !File.Exists(path)) return "";
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset)
                {
                    _offset = 0;
                    _decoder = Encoding.UTF8.GetDecoder();
                    reset = true;
                }
                if (stream.Length == _offset) return "";
                stream.Seek(_offset, SeekOrigin.Begin);
                var buffer = new byte[(int)Math.Min(stream.Length - _offset, MaxChunk)];
                var read = stream.Read(buffer, 0, buffer.Length);
                _offset += read;
                var chars = new char[_decoder.GetCharCount(buffer, 0, read)];
                var count = _decoder.GetChars(buffer, 0, read, chars, 0);
                return new string(chars, 0, count);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
        }
    }

    private sealed class LineSplitter
    {
        private readonly StringBuilder _pending = new();

        public IEnumerable<string> Push(string text)
        {
            var lines = new List<string>();
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    lines.Add(_pending.ToString());
                    _pending.Clear();
                }
                else if (c != '\r') _pending.Append(c);
            }
            return lines;
        }
    }
}
