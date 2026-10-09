using BasisPM.Core.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace BasisPM.Cli.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "basispm-tests", $"cli-{Guid.NewGuid():N}");

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts.SelectMany(p => p.Split('/', '\\'))).ToArray());

    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    public string CreateDir(string relativePath)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
        catch (Exception ex) { DiagnosticLog.Write($"Deleting CLI test directory {Path}", ex); }
    }
}

internal sealed class CliHarness : IDisposable
{
    private readonly TextWriter _out = Console.Out, _error = Console.Error;
    private readonly StringWriter _stdout = new(), _stderr = new();
    private readonly string _directory = Environment.CurrentDirectory;

    public CliHarness(string? catalogJson = null)
    {
        Settings = new UserSettingsService(Temp.Combine("data", "settings.json"));
        StateDirectory = Temp.CreateDir("state");
        var catalogs = catalogJson is null ? null : new CatalogService(new HttpClient(new StubHandler(catalogJson)));
        App = new ConsoleApplication(Settings, new MountRegistry(Temp.CreateDir("data")), StateDirectory, catalogs);
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        Out.Configure(noColorFlag: true);
        Environment.CurrentDirectory = Temp.CreateDir("work");
    }

    public TempDir Temp { get; } = new();
    public UserSettingsService Settings { get; }
    public string StateDirectory { get; }
    public ConsoleApplication App { get; }
    public string Stdout => _stdout.ToString();
    public string Stderr => _stderr.ToString();
    public string All => Stdout + Stderr;

    public async Task<int> RunAsync(params string[] args)
    {
        _stdout.GetStringBuilder().Clear();
        _stderr.GetStringBuilder().Clear();
        return await App.RunCommandAsync(args);
    }

    public string MakeBasis(string folder, string unityVersion = "6000.7.0b4", string manifest = "{ \"dependencies\": { } }")
    {
        Temp.WriteFile($"{folder}/Basis/ProjectSettings/ProjectVersion.txt", $"m_EditorVersion: {unityVersion}\n");
        Temp.CreateDir($"{folder}/Basis/Assets");
        Temp.WriteFile($"{folder}/Basis/Packages/manifest.json", manifest);
        Temp.WriteFile($"{folder}/Basis/Packages/com.basis.framework/package.json", "{ \"name\": \"com.basis.framework\", \"version\": \"0.0.1\", \"displayName\": \"Basis Framework\" }");
        return Temp.Combine(folder);
    }

    public string ReadManifest(string folder) => File.ReadAllText(Temp.Combine(folder, "Basis", "Packages", "manifest.json"));

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _directory;
        Console.SetOut(_out);
        Console.SetError(_error);
        Temp.Dispose();
    }
}
