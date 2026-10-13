using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed partial class UnityHubService
{
    public const string FlatpakId = "com.unity.UnityHub";

    public static IReadOnlyList<string> HubCandidates(OSPlatform os, string home, string programFiles, string programFilesX86)
    {
        if (os == OSPlatform.Windows)
            return new[] { programFiles, programFilesX86 }.Where(f => f.Length > 0).Select(f => Path.Combine(f, "Unity Hub", "Unity Hub.exe")).ToList();
        var user = home.TrimEnd('/');
        if (os == OSPlatform.OSX)
            return new[] { "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub", user + "/Applications/Unity Hub.app/Contents/MacOS/Unity Hub" };
        return new[]
        {
            "/usr/bin/unityhub", "/usr/lib/unityhub/unityhub", "/opt/unityhub/unityhub", "/opt/unityhub/UnityHub.AppImage",
            user + "/Applications/Unity Hub.AppImage", user + "/Applications/UnityHub.AppImage", user + "/.local/bin/unityhub",
            "/var/lib/flatpak/exports/bin/" + FlatpakId, user + "/.local/share/flatpak/exports/bin/" + FlatpakId,
        };
    }

    public static List<string> HubArguments(OSPlatform os, params string[] command)
    {
        var arguments = os == OSPlatform.Linux ? new List<string> { "--headless" } : new List<string> { "--", "--headless" };
        arguments.AddRange(command);
        return arguments;
    }

    public static bool CanRunHeadless(OSPlatform os, string? display, string? waylandDisplay) =>
        os != OSPlatform.Linux || !string.IsNullOrEmpty(display) || !string.IsNullOrEmpty(waylandDisplay);

    public static string? HubLabelArchitecture(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        if (label.Contains("silicon", StringComparison.OrdinalIgnoreCase) || label.Contains("arm", StringComparison.OrdinalIgnoreCase)) return "arm64";
        return label.Contains("intel", StringComparison.OrdinalIgnoreCase) || label.Contains("x64", StringComparison.OrdinalIgnoreCase) || label.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "x86_64" : null;
    }

    public static string? ParseHubInfoExecutable(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("executablePath", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool HeadlessAvailable => CanRunHeadless(Platform.Current, Environment.GetEnvironmentVariable("DISPLAY"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string? HubFromInfo()
    {
        var configs = UnityEditorLocator.HubConfigFolders(Platform.Current, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        foreach (var config in configs)
        {
            try
            {
                var file = Path.Combine(config, "hubInfo.json");
                if (File.Exists(file) && ParseHubInfoExecutable(File.ReadAllText(file)) is { Length: > 0 } exe && File.Exists(exe)) return exe;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write("Reading Unity Hub's hubInfo.json", ex); }
        }
        return null;
    }

    public static string? ResolveOverride(string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return null;
        var path = overridePath.Trim();
        if (File.Exists(path)) return path;
        var inner = Path.Combine(path, "Contents", "MacOS", "Unity Hub");
        return path.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && File.Exists(inner) ? inner : null;
    }

    public string? FindHubPath(string? overridePath = null)
    {
        if (ResolveOverride(overridePath) is { } chosen) return chosen;
        var candidates = HubCandidates(Platform.Current, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        return (Platform.Current == OSPlatform.Linux ? ExecutableFinder.Locate("unityhub") : null) ?? candidates.FirstOrDefault(File.Exists) ?? HubFromInfo();
    }

    public async Task<IReadOnlyList<InstalledEditor>> ListInstalledAsync(string? hubOverride = null, CancellationToken ct = default)
    {
        var found = UnityEditorLocator.FindInstalled();
        return found.Count > 0 ? found : await ListFromHubAsync(hubOverride, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<InstalledEditor>> ListFromHubAsync(string? hubOverride, CancellationToken ct)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null || !HeadlessAvailable) return Array.Empty<InstalledEditor>();

        var (code, stdout, _) = await RunAsync(hub, HubArguments(Platform.Current, "editors", "--installed"), ct).ConfigureAwait(false);
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
                Architecture = HubLabelArchitecture(m.Groups["arch"].Value),
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
        InstalledEditor? Match() => editors.Where(e => string.Equals(e.Version, unityVersion, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Architecture == UnityEditorLocator.NativeArchitecture).FirstOrDefault();
        if (Match() is null) editors.AddRange(await ListFromHubAsync(hubOverride, ct).ConfigureAwait(false));
        var editor = Match();
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
        var hub = RunnableHub(hubOverride);
        var args = HubArguments(Platform.Current, "install", "--version", version, "--changeset", changeset);
        if (Platform.Current == OSPlatform.OSX) args.AddRange(new[] { "-a", UnityEditorLocator.NativeArchitecture });
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

    public async Task<int> InstallModulesAsync(string version, IEnumerable<string> modules, string? hubOverride = null, CancellationToken ct = default, string? architecture = null)
    {
        var hub = RunnableHub(hubOverride);
        var args = HubArguments(Platform.Current, "install-modules", "--version", version);
        if (Platform.Current == OSPlatform.OSX) args.AddRange(new[] { "-a", architecture ?? UnityEditorLocator.NativeArchitecture });
        foreach (var m in modules)
        {
            args.Add("-m");
            args.Add(m);
        }
        args.Add("--childModules");

        var (code, _, _) = await RunAsync(hub, args, ct).ConfigureAwait(false);
        return code;
    }

    private string RunnableHub(string? hubOverride)
    {
        var hub = FindHubPath(hubOverride) ?? throw new InvalidOperationException("Unity Hub not found.");
        if (!HeadlessAvailable) throw new InvalidOperationException("Unity Hub needs a desktop session on Linux. Run this from your desktop, or start it with xvfb-run.");
        return hub;
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
