using System.Diagnostics;
using BasisPM.Core.Services;

namespace BasisPM.Core.Tests.TestSupport;

public sealed class GitSandbox : IDisposable
{
    private readonly TempDir _temp = new("update");

    public GitSandbox()
    {
        Git = new GitService(allowLocalRemotes: true);
        Upstream = _temp.CreateDir("upstream");
        Run(Upstream, "init", "-q", "-b", "developer");
        Run(Upstream, "config", "core.autocrlf", "false");
        Updates = new BasisUpdateService(Git, Upstream);
    }

    public GitService Git { get; }
    public BasisUpdateService Updates { get; }
    public string Upstream { get; }

    public string Combine(params string[] parts) => _temp.Combine(parts);

    public string CommitUpstream(string message, params (string Path, string? Content)[] files)
    {
        foreach (var (path, content) in files) Write(Upstream, path, content);
        Run(Upstream, "add", "-A");
        Run(Upstream, "commit", "-q", "-m", message);
        return Run(Upstream, "rev-parse", "HEAD");
    }

    public string CloneProject(string name = "project", string branch = "developer")
    {
        var path = _temp.Combine(name);
        Run(_temp.Path, "clone", "-q", "-b", branch, Upstream, path);
        Run(path, "config", "core.autocrlf", "false");
        return path;
    }

    public static string Commit(string repo, string message, params (string Path, string? Content)[] files)
    {
        foreach (var (path, content) in files) Write(repo, path, content);
        Run(repo, "add", "-A");
        Run(repo, "commit", "-q", "-m", message);
        return Run(repo, "rev-parse", "HEAD");
    }

    public static void Write(string repo, string relativePath, string? content)
    {
        var full = Path.Combine(repo, relativePath);
        if (content is null)
        {
            if (File.Exists(full)) File.Delete(full);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public static string Read(string repo, string relativePath) => File.ReadAllText(Path.Combine(repo, relativePath));

    public static string Status(string repo) => Run(repo, "status", "--porcelain=v1", "--untracked-files=all");

    public static string Run(string cwd, params string[] args)
    {
        var git = new GitService().FindGit() ?? throw new InvalidOperationException("git not found");
        var psi = new ProcessStartInfo(git)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-c", "protocol.file.allow=always", "-c", "user.name=Tester", "-c", "user.email=tester@example.com", "-c", "core.autocrlf=false" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_ALLOW_PROTOCOL"] = "https:http:git:ssh:file";
        psi.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({p.ExitCode}): {stderr.Result}{stdout.Result}");
        return stdout.Result.TrimEnd();
    }

    public static int RunExitCode(string cwd, params string[] args)
    {
        try { Run(cwd, args); return 0; }
        catch (InvalidOperationException) { return 1; }
    }

    public void Dispose()
    {
        try { BasisInstallService.DeleteFolderAsync(_temp.Path).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Write($"Deleting git sandbox {_temp.Path}", ex); }
        _temp.Dispose();
    }
}
