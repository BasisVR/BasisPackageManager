using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BasisPM.Core.Services;

/// <summary>
/// Resolves a GitHub token for the publish wizard. Prefers the GitHub CLI (<c>gh auth token</c>) so the
/// end user's existing login is reused with nothing stored; falls back to an explicitly-supplied PAT.
/// Callers only ever see a token string, so an OAuth device-flow source can be added here later
/// without changing any of them.
/// </summary>
public sealed class GitHubAuthService
{
    private static readonly string[] WindowsGhPaths =
    {
        @"C:\Program Files\GitHub CLI\gh.exe",
        @"C:\Program Files (x86)\GitHub CLI\gh.exe",
    };

    private string? _cachedGh;
    private string? _pat;

    /// <summary>An explicit personal access token to use when <c>gh</c> is unavailable (kept in memory only).</summary>
    public void SetPersonalAccessToken(string? pat) => _pat = string.IsNullOrWhiteSpace(pat) ? null : pat.Trim();
    public bool HasPersonalAccessToken => _pat is not null;

    public string? FindGh()
    {
        if (_cachedGh is not null && File.Exists(_cachedGh)) return _cachedGh;
        var onPath = ExecutableFinder.Locate("gh");
        if (onPath is not null) return _cachedGh = onPath;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var found = WindowsGhPaths.FirstOrDefault(File.Exists);
            if (found is not null) return _cachedGh = found;
        }
        return null;
    }

    public bool GhAvailable => FindGh() is not null;

    /// <summary>The gh token when logged in, else the supplied PAT, else null (not authenticated).</summary>
    public async Task<string?> GetTokenAsync(CancellationToken ct = default)
    {
        var gh = FindGh();
        if (gh is not null)
        {
            var (code, outText, _) = await RunAsync(gh, new[] { "auth", "token" }, ct).ConfigureAwait(false);
            var token = outText.Trim();
            if (code == 0 && token.Length > 0) return token;
        }
        return _pat;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) =>
        !string.IsNullOrEmpty(await GetTokenAsync(ct).ConfigureAwait(false));

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
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) se.AppendLine(e.Data); };
        try
        {
            if (!p.Start()) return (-1, "", "failed to start");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, so.ToString(), se.ToString());
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
