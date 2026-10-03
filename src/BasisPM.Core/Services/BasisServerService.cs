using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class BasisServerService
{
    public const string SteamAppId = "3157090";

    public BasisServerPaths GetPaths(string repoRoot)
    {
        var project = Path.Combine(repoRoot, "Basis Server", "BasisServerConsole", "BasisNetworkConsole.csproj");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(repoRoot))))[..12];
        var runtime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Basis Package Manager", "Servers", hash);
        var executable = Path.Combine(runtime, OperatingSystem.IsWindows() ? "BasisNetworkConsole.exe" : "BasisNetworkConsole");
        return new BasisServerPaths(project, runtime, executable,
            Path.Combine(runtime, "config", "config.xml"),
            Path.Combine(runtime, "initialresources"),
            Path.Combine(runtime, "defaultlibrary"));
    }

    public async Task<(bool Success, string Output)> BuildAsync(string repoRoot, CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(repoRoot);
        if (!File.Exists(paths.ProjectFile)) return (false, "The Basis server project was not found in this checkout.");
        Directory.CreateDirectory(paths.RuntimeDirectory);
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(paths.ProjectFile)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "publish", paths.ProjectFile, "-c", "Release", "-o", paths.RuntimeDirectory })
            psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = ((await stdout) + Environment.NewLine + (await stderr)).Trim();
        return (process.ExitCode == 0, output);
    }

    public async Task<bool> HasRequiredDotNetSdkAsync(CancellationToken cancellationToken = default)
    {
        var dotnet = ExecutableFinder.Locate("dotnet");
        if (dotnet is null) return false;
        var psi = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--list-sdks");
        using var process = Process.Start(psi);
        if (process is null) return false;
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        return process.ExitCode == 0 && output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.TrimStart().StartsWith("10.", StringComparison.Ordinal));
    }

    public static bool IsLocalHost(string host)
    {
        host = host.Trim().Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "::1" or "0.0.0.0" or "::";
    }

    public static async Task<bool> CanConnectAsync(string host, ushort port, TimeSpan timeout)
    {
        host = host.Trim().Trim('[', ']');
        if (host == "0.0.0.0") host = "127.0.0.1";
        if (host == "::") host = "::1";
        try
        {
            using var client = new TcpClient();
            using var cancellation = new CancellationTokenSource(timeout);
            await client.ConnectAsync(host, port, cancellation.Token);
            return true;
        }
        catch { return false; }
    }

    public Process Start(string repoRoot)
    {
        var paths = GetPaths(repoRoot);
        if (!File.Exists(paths.ExecutablePath)) throw new FileNotFoundException("Build the Basis server first.", paths.ExecutablePath);
        Directory.CreateDirectory(paths.RuntimeDirectory);
        return Process.Start(new ProcessStartInfo(paths.ExecutablePath)
        {
            WorkingDirectory = paths.RuntimeDirectory,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Could not start the Basis server.");
    }

    public IReadOnlyList<ServerConfigField> LoadConfig(string repoRoot)
    {
        var file = GetPaths(repoRoot).ConfigFile;
        if (!File.Exists(file)) return Array.Empty<ServerConfigField>();
        var root = XDocument.Load(file, LoadOptions.PreserveWhitespace).Root
            ?? throw new InvalidDataException("config.xml has no root element.");
        return root.Elements().Select(element => new ServerConfigField(
            element.Name.LocalName,
            element.Value,
            PreviousComment(element))).ToList();
    }

    public void SaveConfig(string repoRoot, IEnumerable<ServerConfigField> fields)
    {
        var file = GetPaths(repoRoot).ConfigFile;
        if (!File.Exists(file)) throw new FileNotFoundException("Run the server once to create config.xml.", file);
        var document = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("config.xml has no root element.");
        var values = fields.ToDictionary(x => x.Name, x => x.Value, StringComparer.Ordinal);
        foreach (var element in root.Elements())
            if (values.TryGetValue(element.Name.LocalName, out var value)) element.Value = value;
        var temp = file + ".tmp";
        document.Save(temp, SaveOptions.DisableFormatting);
        File.Move(temp, file, true);
    }

    public string AddDefaultLibraryItem(string repoRoot, int mode, string url, string password)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A content URL is required.");
        var folder = GetPaths(repoRoot).DefaultLibraryDirectory;
        Directory.CreateDirectory(folder);
        var path = UniqueXmlPath(folder, mode switch { 0 => "avatar", 1 => "world", 2 => "prop", _ => "item" });
        new XDocument(new XElement("BasisDefaultLibraryConfiguration",
            new XElement("Mode", mode), new XElement("Url", url.Trim()), new XElement("Password", password ?? ""))).Save(path);
        return path;
    }

    public string AddInitialResource(string repoRoot, int mode, string url, string password)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A content URL is required.");
        var folder = GetPaths(repoRoot).InitialResourcesDirectory;
        Directory.CreateDirectory(folder);
        var path = UniqueXmlPath(folder, "resource");
        new XDocument(new XElement("BasisLoadableConfiguration",
            new XElement("Mode", mode), new XElement("LoadedNetID", ""),
            new XElement("UnlockPassword", password ?? ""), new XElement("CombinedURL", url.Trim()),
            new XElement("IsLocalLoad", false),
            new XElement("PositionX", 0), new XElement("PositionY", 0), new XElement("PositionZ", 0),
            new XElement("QuaternionX", 0), new XElement("QuaternionY", 0), new XElement("QuaternionZ", 0), new XElement("QuaternionW", 1),
            new XElement("ScaleX", 1), new XElement("ScaleY", 1), new XElement("ScaleZ", 1),
            new XElement("Persist", false))).Save(path);
        return path;
    }

    public IReadOnlyList<ServerContentFile> ListContent(string repoRoot)
    {
        var paths = GetPaths(repoRoot);
        return Enumerate(paths.DefaultLibraryDirectory, true)
            .Concat(Enumerate(paths.InitialResourcesDirectory, false))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void RemoveContent(string repoRoot, ServerContentFile item)
    {
        var paths = GetPaths(repoRoot);
        var fullPath = Path.GetFullPath(item.FullPath);
        var allowed = new[] { paths.DefaultLibraryDirectory, paths.InitialResourcesDirectory }
            .Any(folder => fullPath.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, Platform.PathComparison()));
        if (!allowed || !string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not managed server content.");
        if (File.Exists(item.FullPath)) File.Delete(item.FullPath);
    }

    public bool LaunchSteamClient(string connection)
    {
        var steam = FindSteamExecutable();
        if (steam is null) return false;
        var psi = new ProcessStartInfo(steam) { UseShellExecute = false };
        psi.ArgumentList.Add("-applaunch");
        psi.ArgumentList.Add(SteamAppId);
        psi.ArgumentList.Add("--connection=" + connection);
        return Process.Start(psi) is not null;
    }

    public static string? FindSteamExecutable()
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steam.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam", "steam.exe"));
        }
        else if (OperatingSystem.IsMacOS()) candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Application Support/Steam/Steam.AppBundle/Steam/Contents/MacOS/steam_osx"));
        else candidates.AddRange(new[] { "/usr/bin/steam", "/usr/local/bin/steam" });
        return candidates.FirstOrDefault(File.Exists) ?? ExecutableFinder.Locate("steam");
    }

    public static string BuildConnection(string host, ushort port, string? password)
    {
        host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        if (host.Contains(':') && !host.StartsWith('[')) host = $"[{host}]";
        return $"{host}:{port}" + (string.IsNullOrEmpty(password) ? "" : "#" + password);
    }

    private static IEnumerable<ServerContentFile> Enumerate(string folder, bool library) =>
        Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.xml", SearchOption.TopDirectoryOnly).Select(x => new ServerContentFile(Path.GetFileName(x), x, library))
            : Array.Empty<ServerContentFile>();

    private static string UniqueXmlPath(string folder, string prefix) =>
        Path.Combine(folder, $"{prefix}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}.xml");

    private static string PreviousComment(XElement element)
    {
        var comments = element.NodesBeforeSelf().Reverse().TakeWhile(x => x is XComment || x is XText t && string.IsNullOrWhiteSpace(t.Value))
            .OfType<XComment>().Select(x => Regex.Replace(x.Value.Trim(), @"\s+", " ")).Reverse();
        return string.Join(" ", comments);
    }
}
