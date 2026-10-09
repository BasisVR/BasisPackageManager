using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed partial class UnityHubService
{
    private static readonly string[] WindowsHubPaths =
    {
        @"C:\Program Files\Unity Hub\Unity Hub.exe",
        @"C:\Program Files (x86)\Unity Hub\Unity Hub.exe",
    };

    private static readonly string[] MacHubPaths =
    {
        "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub",
    };

    /// <summary>
    /// Well-known Linux Unity Hub locations. The apt/.deb package drops a <c>/usr/bin/unityhub</c>
    /// wrapper (found via PATH), but the official Linux Hub also ships as an AppImage that isn't on
    /// PATH — so probe the common AppImage / opt locations too.
    /// </summary>
    private static IEnumerable<string> LinuxHubCandidates()
    {
        yield return "/usr/bin/unityhub";
        yield return "/opt/unityhub/unityhub";
        yield return "/opt/unityhub/UnityHub.AppImage";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            yield return Path.Combine(home, "Applications", "Unity Hub.AppImage");
            yield return Path.Combine(home, "Applications", "UnityHub.AppImage");
            yield return Path.Combine(home, ".local", "bin", "unityhub");
        }
    }

    public string? FindHubPath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return WindowsHubPaths.FirstOrDefault(File.Exists);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return MacHubPaths.FirstOrDefault(File.Exists);
        }

        // Linux: the PATH wrapper first, then the well-known deb/AppImage locations.
        return ExecutableFinder.Locate("unityhub") ?? LinuxHubCandidates().FirstOrDefault(File.Exists);
    }

    public async Task<IReadOnlyList<InstalledEditor>> ListInstalledAsync(string? hubOverride = null, CancellationToken ct = default)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) return Array.Empty<InstalledEditor>();

        var (code, stdout, _) = await RunAsync(hub, new[] { "--", "--headless", "editors", "--installed" }, ct).ConfigureAwait(false);
        if (code != 0 || string.IsNullOrWhiteSpace(stdout)) return Array.Empty<InstalledEditor>();

        var list = new List<InstalledEditor>();
        foreach (var line in stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = HubLineRegex().Match(line);
            if (!m.Success) continue;
            list.Add(new InstalledEditor
            {
                Version = m.Groups["ver"].Value,
                Path = m.Groups["path"].Value.Trim(),
            });
        }
        return list;
    }

    /// <summary>
    /// Launches the editor matching <paramref name="unityVersion"/> on the project. Considers both the
    /// Hub-installed editors and any <paramref name="extraEditors"/> the user registered by hand (so it
    /// works with no Unity Hub). False if no matching editor is found.
    /// </summary>
    public async Task<bool> OpenProjectAsync(string projectPath, string? unityVersion, string? hubOverride = null, IEnumerable<InstalledEditor>? extraEditors = null, CancellationToken ct = default)
    {
        var editors = new List<InstalledEditor>(await ListInstalledAsync(hubOverride, ct).ConfigureAwait(false));
        if (extraEditors is not null) editors.AddRange(extraEditors);
        var editor = editors.FirstOrDefault(e => string.Equals(e.Version, unityVersion, StringComparison.OrdinalIgnoreCase));
        // editor.Path is an executable on Windows/Linux, but on macOS Hub may report a .app bundle
        // (a directory) — accept either, and let GuiAppSpec `open` the bundle there.
        if (editor is null || (!File.Exists(editor.Path) && !Directory.Exists(editor.Path))) return false;
        var spec = AppLauncher.GuiAppSpec(Platform.Current, editor.Path, new[] { "-projectPath", projectPath });
        return Process.Start(spec.ToStartInfo()) is not null;
    }

    private static string? ResolveEditorExecutable(string path)
    {
        if (File.Exists(path)) return path;
        if (Directory.Exists(path))
        {
            var mac = Path.Combine(path, "Contents", "MacOS", "Unity");
            if (File.Exists(mac)) return mac;
        }
        return null;
    }

    /// <summary>Opens the Unity Hub GUI — the fallback when the matching editor isn't installed. False if Hub isn't found.</summary>
    public bool OpenHub(string? hubOverride = null)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) return false;
        // FindHubPath returns the executable (on macOS, the inner Mach-O binary). LaunchGuiApp opens it
        // correctly per-OS: on macOS it `open`s the enclosing .app rather than the inner binary (which
        // UseShellExecute=true would hand to `open` and silently fail).
        return AppLauncher.LaunchGuiApp(hub);
    }

    public async Task<int> InstallEditorAsync(string version, string changeset, IEnumerable<string>? modules, string? hubOverride = null, CancellationToken ct = default)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) throw new InvalidOperationException("Unity Hub not found.");

        var args = new List<string>
        {
            "--", "--headless", "install",
            "--version", version,
            "--changeset", changeset,
        };
        if (modules is not null)
        {
            foreach (var m in modules)
            {
                args.Add("-m");
                args.Add(m);
            }
        }
        if (args.Contains("-m")) args.Add("--childModules");

        var (code, _, _) = await RunAsync(hub, args, ct).ConfigureAwait(false);
        return code;
    }

    public async Task<int> InstallModulesAsync(string version, IEnumerable<string> modules, string? hubOverride = null, CancellationToken ct = default)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) throw new InvalidOperationException("Unity Hub not found.");

        var args = new List<string> { "--", "--headless", "install-modules", "--version", version };
        foreach (var m in modules)
        {
            args.Add("-m");
            args.Add(m);
        }
        args.Add("--childModules");

        var (code, _, _) = await RunAsync(hub, args, ct).ConfigureAwait(false);
        return code;
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
        p.Exited += (_, _) => exited.TrySetResult();
        if (!p.Start()) throw new InvalidOperationException($"Failed to start {exe}");
        using var stop = ct.Register(() =>
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { DiagnosticLog.Write("Stopping a cancelled Unity Hub command", ex); }
        });
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (p.HasExited) exited.TrySetResult();
        await exited.Task.WaitAsync(ct).ConfigureAwait(false);
        using (var grace = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            grace.CancelAfter(TimeSpan.FromSeconds(2));
            try { await p.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        lock (stdout) lock (stderr) return (p.ExitCode, stdout.ToString(), stderr.ToString());
    }

    [GeneratedRegex(@"^(?<ver>\S+)\s*(?:\((?<arch>[^)]+)\))?\s*,?\s*installed at\s+(?<path>.+)$")]
    private static partial Regex HubLineRegex();
}
