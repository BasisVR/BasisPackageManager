using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BasisPM.Core.Services;

public static class TerminalLauncher
{
    private static readonly (string Program, string[] Before)[] Known =
    {
        ("ptyxis", new[] { "--" }), ("kgx", new[] { "--" }), ("gnome-terminal", new[] { "--" }), ("konsole", new[] { "-e" }), ("xfce4-terminal", new[] { "-x" }),
        ("mate-terminal", new[] { "-x" }), ("cosmic-term", new[] { "-e" }), ("tilix", new[] { "-e" }), ("terminator", new[] { "-x" }), ("qterminal", new[] { "-e" }),
        ("lxterminal", new[] { "-e" }), ("deepin-terminal", new[] { "-e" }), ("alacritty", new[] { "-e" }), ("kitty", Array.Empty<string>()), ("wezterm", new[] { "start", "--" }),
        ("foot", Array.Empty<string>()), ("xterm", new[] { "-e" }), ("urxvt", new[] { "-e" }), ("st", new[] { "-e" }),
    };

    private static readonly (string Desktop, string[] Programs)[] DesktopTerminals =
    {
        ("KDE", new[] { "konsole" }), ("GNOME", new[] { "ptyxis", "kgx", "gnome-terminal" }), ("XFCE", new[] { "xfce4-terminal" }), ("MATE", new[] { "mate-terminal" }),
        ("X-CINNAMON", new[] { "gnome-terminal" }), ("COSMIC", new[] { "cosmic-term" }), ("LXQT", new[] { "qterminal" }), ("LXDE", new[] { "lxterminal" }), ("DEEPIN", new[] { "deepin-terminal" }),
    };

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static string Script(string workingDirectory, string executable, string pidFile, IReadOnlyDictionary<string, string>? environment = null) =>
        "#!/bin/sh\n" +
        "echo $$ > " + Quote(pidFile) + "\n" +
        string.Concat((environment ?? new Dictionary<string, string>()).Where(e => IsVariableName(e.Key)).Select(e => "export " + e.Key + "=" + Quote(e.Value) + "\n")) +
        "cd " + Quote(workingDirectory) + " || exit 1\n" +
        "exec " + Quote(executable) + "\n";

    private static bool IsVariableName(string name) => name.Length > 0 && !char.IsDigit(name[0]) && name.All(c => c == '_' || char.IsAsciiLetterOrDigit(c));

    public static IReadOnlyList<LaunchSpec> Specs(OSPlatform os, string script, string? terminal, string? desktop, Func<string, bool> installed)
    {
        if (os == OSPlatform.Windows) return Array.Empty<LaunchSpec>();
        if (os == OSPlatform.OSX) return new[] { new LaunchSpec("open", new[] { "-a", "Terminal", script }, UseShellExecute: false) };
        var specs = new List<LaunchSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string program, IEnumerable<string> before)
        {
            if (program.Length == 0 || !seen.Add(program) || !installed(program)) return;
            specs.Add(new LaunchSpec(program, before.Append(script).ToList(), UseShellExecute: false));
        }
        Add("xdg-terminal-exec", Array.Empty<string>());
        if (!string.IsNullOrWhiteSpace(terminal) && !terminal.Contains(' ')) Add(terminal.Trim(), KnownArguments(terminal.Trim()) ?? new[] { "-e" });
        Add("x-terminal-emulator", new[] { "-e" });
        var desktops = (desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var (name, programs) in DesktopTerminals)
            if (desktops.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase)))
                foreach (var program in programs) Add(program, KnownArguments(program)!);
        foreach (var (program, before) in Known) Add(program, before);
        return specs;
    }

    public static bool IsAvailable() => Platform.Current != OSPlatform.Linux || Candidates().Count > 0;

    public static async Task<Process> StartAsync(string workingDirectory, string executable, CancellationToken ct = default, IReadOnlyDictionary<string, string>? environment = null)
    {
        var folder = Path.Combine(AppDataPaths.LocalRoot, "terminal");
        Directory.CreateDirectory(folder);
        DeleteStale(folder);
        var id = Guid.NewGuid().ToString("N")[..12];
        var pidFile = Path.Combine(folder, id + ".pid");
        var script = Path.Combine(folder, id + (Platform.Current == OSPlatform.OSX ? ".command" : ".sh"));
        await File.WriteAllTextAsync(script, Script(workingDirectory, executable, pidFile, environment), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var spec in Specs(Platform.Current, script, Environment.GetEnvironmentVariable("TERMINAL"), Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), p => ExecutableFinder.Locate(p) is not null))
        {
            Process? launcher;
            try { launcher = Process.Start(spec.ToStartInfo()); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { DiagnosticLog.Write($"Opening {spec.FileName} for the Basis server", ex); continue; }
            int? pid;
            bool failed;
            using (launcher) { (pid, failed) = await WaitForPidAsync(pidFile, launcher, ct).ConfigureAwait(false); }
            if (failed) continue;
            TryDelete(pidFile);
            if (pid is null) break;
            TryDelete(script);
            try { return Process.GetProcessById(pid.Value); }
            catch (ArgumentException) { throw new InvalidOperationException("The Basis server closed right after it started. Run 'basispm server-run' in a terminal to see why."); }
        }
        throw new InvalidOperationException(Platform.Current == OSPlatform.Linux
            ? "No terminal app opened the server. Install one (for example GNOME Terminal or Konsole), or run 'basispm server-run' in a terminal."
            : "Terminal didn't start the server.");
    }

    public static bool Interrupt(Process process)
    {
        if (OperatingSystem.IsWindows()) return process.CloseMainWindow();
        try
        {
            using var kill = Process.Start(new ProcessStartInfo(ExecutableFinder.Locate("kill") ?? "/bin/kill") { ArgumentList = { "-s", "INT", process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) }, UseShellExecute = false, CreateNoWindow = true });
            return kill is not null && kill.WaitForExit(5000) && kill.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { DiagnosticLog.Write("Asking the Basis server to stop", ex); return false; }
    }

    private static async Task<(int? Pid, bool Failed)> WaitForPidAsync(string pidFile, Process? launcher, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (File.Exists(pidFile) && await File.ReadAllTextAsync(pidFile, ct).ConfigureAwait(false) is { } text && text.EndsWith('\n') && int.TryParse(text.Trim(), out var pid) && pid > 0) return (pid, false);
            if (launcher is { HasExited: true, ExitCode: not 0 }) return (null, true);
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return (null, false);
    }

    private static void DeleteStale(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddMinutes(-10)) TryDelete(file);
    }

    private static string[]? KnownArguments(string program) => Known.FirstOrDefault(k => k.Program == Path.GetFileName(program)).Before;

    private static List<string> Candidates() =>
        Specs(OSPlatform.Linux, "", Environment.GetEnvironmentVariable("TERMINAL"), Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"), p => ExecutableFinder.Locate(p) is not null).Select(s => s.FileName).ToList();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting {path}", ex); }
    }
}
