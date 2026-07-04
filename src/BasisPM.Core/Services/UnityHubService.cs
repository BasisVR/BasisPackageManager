using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public string? FindHubPath(string? overridePath = null)
    {
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            return overridePath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsHubPaths.FirstOrDefault(File.Exists);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacHubPaths.FirstOrDefault(File.Exists);

        return WhichOnPath("unityhub");
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

    /// <summary>Launches the installed Unity editor matching <paramref name="unityVersion"/> on the project. False if that editor isn't installed.</summary>
    public async Task<bool> OpenProjectAsync(string projectPath, string? unityVersion, string? hubOverride = null, CancellationToken ct = default)
    {
        var editors = await ListInstalledAsync(hubOverride, ct).ConfigureAwait(false);
        var editor = editors.FirstOrDefault(e => string.Equals(e.Version, unityVersion, StringComparison.OrdinalIgnoreCase));
        if (editor is null || !File.Exists(editor.Path)) return false;
        Process.Start(new ProcessStartInfo { FileName = editor.Path, UseShellExecute = false, ArgumentList = { "-projectPath", projectPath } });
        return true;
    }

    /// <summary>Opens the Unity Hub GUI — the fallback when the matching editor isn't installed. False if Hub isn't found.</summary>
    public bool OpenHub(string? hubOverride = null)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) return false;
        Process.Start(new ProcessStartInfo { FileName = hub, UseShellExecute = true });
        return true;
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

        var (code, _, _) = await RunAsync(hub, args, ct).ConfigureAwait(false);
        return code;
    }

    public async Task<int> UninstallEditorAsync(string version, string? hubOverride = null, CancellationToken ct = default)
    {
        var hub = FindHubPath(hubOverride);
        if (hub is null) throw new InvalidOperationException("Unity Hub not found.");

        var (code, _, _) = await RunAsync(hub,
            new[] { "--", "--headless", "uninstall", "--version", version },
            ct).ConfigureAwait(false);

        if (code == 0) return 0;

        var installs = await ListInstalledAsync(hubOverride, ct).ConfigureAwait(false);
        var match = installs.FirstOrDefault(e => string.Equals(e.Version, version, StringComparison.Ordinal));
        if (match is not null && Directory.Exists(match.Path))
        {
            var editorRoot = ResolveEditorRoot(match.Path);
            if (editorRoot is not null && Directory.Exists(editorRoot))
            {
                Directory.Delete(editorRoot, recursive: true);
                return 0;
            }
        }
        return code;
    }

    private static string? ResolveEditorRoot(string path)
    {
        var dir = new DirectoryInfo(path);
        while (dir is not null)
        {
            if (string.Equals(dir.Name, "Editor", StringComparison.OrdinalIgnoreCase) && dir.Parent is not null)
                return dir.Parent.FullName;
            dir = dir.Parent;
        }
        return null;
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
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string? WhichOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    [GeneratedRegex(@"^(?<ver>\S+)\s*(?:\((?<arch>[^)]+)\))?\s*,?\s*installed at\s+(?<path>.+)$")]
    private static partial Regex HubLineRegex();
}
