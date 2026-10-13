using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BasisPM.Core.Services;

public enum ServerConsoleLevel { Info, Warning, Error, Input, Notice }

public sealed record ServerConsoleLine(DateTimeOffset Time, ServerConsoleLevel Level, string Text);

public sealed class ServerConsoleSession : IDisposable
{
    public const string ShutdownCommand = "/shutdown";
    public const int MaxLineLength = 8000;
    public static readonly TimeSpan PartialLineDelay = TimeSpan.FromMilliseconds(250);

    private static readonly Regex Escape = new(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LevelTag = new(@"^\[[^\]]*\]\s*\[(INFO|WARNING|ERROR)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Process _process;
    private readonly Action<ServerConsoleLine>? _onLine;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inputGate = new();
    private readonly object _lineGate = new();
    private bool _inputClosed;

    private ServerConsoleSession(Process process, Action<ServerConsoleLine>? onLine)
    {
        _process = process;
        _onLine = onLine;
        Id = process.Id;
        Started = DateTimeOffset.Now;
    }

    public int Id { get; }
    public DateTimeOffset Started { get; }
    public bool HasExited => _exited.Task.IsCompleted;
    public int? ExitCode => _exited.Task.IsCompletedSuccessfully ? _exited.Task.Result : null;
    public Task<int> Exited => _exited.Task;
    public bool StopRequested { get; private set; }

    public static ServerConsoleSession Start(ProcessStartInfo startInfo, Action<ServerConsoleLine>? onLine = null, Encoding? encoding = null)
    {
        encoding ??= ChildConsoleEncoding();
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding = encoding;
        startInfo.StandardOutputEncoding = encoding;
        startInfo.StandardErrorEncoding = encoding;
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        ChildProcessJob.Add(process);
        var session = new ServerConsoleSession(process, onLine);
        AppDomain.CurrentDomain.ProcessExit += session.OnHostExit;
        session.Pump();
        return session;
    }

    public static (ProcessStartInfo StartInfo, Encoding Encoding) ForExecutable(string executable, string workingDirectory)
    {
        if (OperatingSystem.IsWindows() && executable.IndexOfAny(new[] { '"', '%', '\r', '\n' }) < 0)
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } comSpec && File.Exists(comSpec) ? comSpec : "cmd.exe";
            return (new ProcessStartInfo(shell)
            {
                Arguments = "/d /s /c \"chcp 65001 >nul & \"" + executable + "\"\"",
                WorkingDirectory = workingDirectory,
            }, new UTF8Encoding(false));
        }
        return (new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory }, ChildConsoleEncoding());
    }

    public static Encoding ChildConsoleEncoding()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var codePage = GetOEMCP();
                if (codePage > 0 && codePage != 65001) return Encoding.GetEncoding(codePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or EntryPointNotFoundException or DllNotFoundException)
            {
                DiagnosticLog.Write("Choosing the console encoding for the Basis server", ex);
            }
        }
        return new UTF8Encoding(false);
    }

    public static ServerConsoleLevel LevelOf(string text, bool fromError)
    {
        if (fromError) return ServerConsoleLevel.Error;
        var match = LevelTag.Match(text);
        if (!match.Success) return ServerConsoleLevel.Info;
        return match.Groups[1].Value switch
        {
            "WARNING" => ServerConsoleLevel.Warning,
            "ERROR" => ServerConsoleLevel.Error,
            _ => ServerConsoleLevel.Info,
        };
    }

    public static string Clean(string text)
    {
        var plain = text.IndexOf('\x1B') >= 0 ? Escape.Replace(text, "") : text;
        if (plain.Any(c => char.IsControl(c) && c != '\t')) plain = new string(plain.Where(c => !char.IsControl(c) || c == '\t').ToArray());
        return plain.Length > MaxLineLength ? plain[..MaxLineLength] : plain;
    }

    public bool SendLine(string text)
    {
        lock (_inputGate)
        {
            if (_inputClosed || HasExited) return false;
            try
            {
                _process.StandardInput.WriteLine(text);
                _process.StandardInput.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                DiagnosticLog.Write("Sending a command to the Basis server", ex);
                _inputClosed = true;
                return false;
            }
        }
    }

    public async Task<int> StopAsync(string? quitCommand, TimeSpan grace, CancellationToken ct = default)
    {
        StopRequested = true;
        if (HasExited) return await _exited.Task.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(quitCommand) && SendLine(quitCommand))
        {
            var finished = await Task.WhenAny(_exited.Task, Task.Delay(grace, ct)).ConfigureAwait(false);
            if (finished == _exited.Task) return await _exited.Task.ConfigureAwait(false);
        }
        Kill();
        return await _exited.Task.ConfigureAwait(false);
    }

    public void Kill()
    {
        StopRequested = true;
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            DiagnosticLog.Write("Stopping the Basis server process", ex);
        }
    }

    public void Dispose()
    {
        if (!HasExited) Kill();
        _process.Dispose();
    }

    private void Pump()
    {
        var output = Task.Run(() => ReadAsync(_process.StandardOutput, false));
        var error = Task.Run(() => ReadAsync(_process.StandardError, true));
        _ = FinishAsync(output, error);
    }

    private async Task FinishAsync(Task output, Task error)
    {
        try { await Task.WhenAll(output, error).ConfigureAwait(false); }
        catch (Exception ex) { DiagnosticLog.Write("Reading the Basis server output", ex); }
        int code;
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            code = _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            DiagnosticLog.Write("Waiting for the Basis server to exit", ex);
            code = -1;
        }
        lock (_inputGate) _inputClosed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnHostExit;
        _exited.TrySetResult(code);
    }

    private void OnHostExit(object? sender, EventArgs e) => Kill();

    private async Task ReadAsync(StreamReader reader, bool fromError)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var pendingCarriage = false;
        while (true)
        {
            var read = reader.ReadAsync(buffer, 0, buffer.Length);
            if (line.Length > 0 && !read.IsCompleted && await Task.WhenAny(read, Task.Delay(PartialLineDelay)).ConfigureAwait(false) != read)
            {
                Emit(line.ToString(), fromError);
                line.Clear();
            }
            int count;
            try { count = await read.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                DiagnosticLog.Write("Reading the Basis server output", ex);
                break;
            }
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '\n')
                {
                    Emit(line.ToString(), fromError);
                    line.Clear();
                    pendingCarriage = false;
                    continue;
                }
                if (pendingCarriage)
                {
                    pendingCarriage = false;
                    if (line.Length > 0)
                    {
                        Emit(line.ToString(), fromError);
                        line.Clear();
                    }
                }
                if (c == '\r') pendingCarriage = true;
                else if (line.Length < MaxLineLength) line.Append(c);
            }
        }
        if (line.Length > 0) Emit(line.ToString(), fromError);
    }

    private void Emit(string raw, bool fromError)
    {
        var text = Clean(raw);
        if (fromError && text.Length == 0) return;
        var entry = new ServerConsoleLine(DateTimeOffset.Now, LevelOf(text, fromError), text);
        lock (_lineGate)
        {
            try { _onLine?.Invoke(entry); }
            catch (Exception ex) { DiagnosticLog.Write("Showing a line of Basis server output", ex); }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern int GetOEMCP();
}

internal static class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _failed;

    public static void Add(Process process)
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (Gate)
        {
            if (_failed) return;
            try
            {
                if (_job == IntPtr.Zero && !Create()) return;
                if (!AssignProcessToJobObject(_job, process.Handle))
                    DiagnosticLog.Write("Tying the Basis server to this app", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or EntryPointNotFoundException or DllNotFoundException)
            {
                DiagnosticLog.Write("Tying the Basis server to this app", ex);
            }
        }
    }

    private static bool Create()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            _failed = true;
            DiagnosticLog.Write("Creating a job object for the Basis server", new Win32Exception(Marshal.GetLastWin32Error()));
            return false;
        }
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimit>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, pointer, (uint)size))
            {
                _failed = true;
                DiagnosticLog.Write("Configuring the job object for the Basis server", new Win32Exception(Marshal.GetLastWin32Error()));
                CloseHandle(job);
                return false;
            }
        }
        finally { Marshal.FreeHGlobal(pointer); }
        _job = job;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
