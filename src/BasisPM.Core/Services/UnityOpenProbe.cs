using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BasisPM.Core.Services;

public static class UnityOpenProbe
{
    private const int VnodePathInfo = 9, VnodeInfoSize = 152, MaxPath = 1024;

    public static bool IsOpen(string unityProjectPath)
    {
        var project = Path.GetFullPath(unityProjectPath);
        var lockFile = Path.Combine(project, "Temp", "UnityLockfile");
        if (!File.Exists(lockFile)) return false;
        if (IsLocked(lockFile)) return true;
        var processes = UnityProcesses();
        try
        {
            if (processes.Any(p => SameFolder(WorkingDirectory(p.Id), project, Platform.Current))) return true;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
        return EditorInstancePid(project) is { } pid && IsUnityProcess(pid) && WorkingDirectory(pid) is not { Length: > 0 };
    }

    public static bool SameFolder(string? folder, string project, OSPlatform os) =>
        !string.IsNullOrEmpty(folder) && string.Equals(Trim(folder), Trim(project), Platform.PathComparison(os));

    public static int? ParseEditorInstancePid(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("process_id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var pid) && pid > 0 ? pid : null;
        }
        catch (JsonException) { return null; }
    }

    public static string? CwdFromVnodePathInfo(byte[] buffer, int length)
    {
        if (length < VnodeInfoSize + 1 || buffer.Length < length) return null;
        var end = Array.IndexOf(buffer, (byte)0, VnodeInfoSize, Math.Min(MaxPath, length - VnodeInfoSize));
        var count = (end < 0 ? Math.Min(MaxPath, length - VnodeInfoSize) : end - VnodeInfoSize);
        return count > 0 ? Encoding.UTF8.GetString(buffer, VnodeInfoSize, count) : null;
    }

    private static string Trim(string folder) => folder.Length > 1 ? folder.TrimEnd('/', '\\') : folder;

    private static bool IsLocked(string lockFile)
    {
        try
        {
            using var probe = new FileStream(lockFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && (ex.HResult is 11 or 35 || (ex.HResult & 0xFFFF) is 32 or 33)) { return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static Process[] UnityProcesses()
    {
        try { return Process.GetProcessesByName("Unity"); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { DiagnosticLog.Write("Listing Unity processes", ex); return Array.Empty<Process>(); }
    }

    private static string? WorkingDirectory(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux()) return new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget;
            if (!OperatingSystem.IsMacOS()) return null;
            var buffer = new byte[2 * (VnodeInfoSize + MaxPath)];
            return CwdFromVnodePathInfo(buffer, ProcPidInfo(pid, VnodePathInfo, 0, buffer, buffer.Length));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    private static int? EditorInstancePid(string project)
    {
        try
        {
            var file = Path.Combine(project, "Library", "EditorInstance.json");
            return File.Exists(file) ? ParseEditorInstancePid(File.ReadAllText(file)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsUnityProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) && !process.ProcessName.Contains("Hub", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "proc_pidinfo")]
    private static extern int ProcPidInfo(int pid, int flavor, ulong argument, byte[] buffer, int bufferSize);
}
