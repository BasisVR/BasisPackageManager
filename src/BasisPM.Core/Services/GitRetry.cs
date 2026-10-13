using System.Text.RegularExpressions;

namespace BasisPM.Core.Services;

public enum GitRetryReason { None, LockBusy, Network }

public sealed record GitRetryPolicy(IReadOnlyList<TimeSpan> LockDelays, IReadOnlyList<TimeSpan> NetworkDelays)
{
    public static readonly GitRetryPolicy Default = new(
        new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) },
        new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) });

    public static readonly GitRetryPolicy None = new(Array.Empty<TimeSpan>(), Array.Empty<TimeSpan>());

    public IReadOnlyList<TimeSpan> DelaysFor(GitRetryReason reason) => reason switch
    {
        GitRetryReason.LockBusy => LockDelays,
        GitRetryReason.Network => NetworkDelays,
        _ => Array.Empty<TimeSpan>(),
    };
}

public static class GitRetry
{
    private static readonly string[] NetworkSigns =
    {
        "could not resolve host", "could not resolve proxy", "temporary failure in name resolution", "failed to connect to",
        "timed out", "connection reset", "connection was reset", "connection was aborted", "connection refused", "recv failure",
        "send failure", "remote end hung up unexpectedly", "early eof", "rpc failed", "unexpected disconnect while reading sideband packet",
        "transfer closed with outstanding read data remaining", "empty reply from server", "was not closed cleanly", "ssl_error_syscall",
        "gnutls_handshake() failed", "failed to receive handshake", "tls connection was non-properly terminated", "index-pack failed",
        "invalid index-pack output",
    };

    private static readonly string[] ConnectSigns =
    {
        "could not resolve host", "could not resolve proxy", "temporary failure in name resolution", "failed to connect to",
        "connection refused", "gnutls_handshake() failed", "failed to receive handshake",
    };

    private static readonly string[] PermanentSigns =
    {
        "repository not found", "authentication failed", "could not read username", "could not read password", "permission denied",
        "does not appear to be a git repository", "not found in upstream", "returned error: 401", "returned error: 403", "returned error: 404",
        "already exists and is not an empty directory", "terminal prompts disabled", "access denied", "invalid username or password",
    };

    private static readonly string[] FetchRefRaceSigns =
    {
        "cannot lock ref", "failed: reference already exists", "failed: incorrect old value provided",
    };

    private static readonly Regex TransientStatus = new(@"returned error: (?:408|429|5\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string? CommandOf(IEnumerable<string> args)
    {
        using var arg = args.GetEnumerator();
        while (arg.MoveNext())
        {
            if (arg.Current == "-c") { arg.MoveNext(); continue; }
            if (arg.Current.StartsWith('-')) continue;
            return arg.Current;
        }
        return null;
    }

    public static GitRetryReason Classify(IEnumerable<string> args, string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return GitRetryReason.None;
        var command = CommandOf(args);
        if (IsLockBusy(command, stderr)) return GitRetryReason.LockBusy;
        var signs = command switch
        {
            "clone" or "fetch" or "ls-remote" => NetworkSigns,
            "push" => ConnectSigns,
            _ => null,
        };
        if (signs is null || ContainsAny(stderr, PermanentSigns)) return GitRetryReason.None;
        return ContainsAny(stderr, signs) || (command != "push" && TransientStatus.IsMatch(stderr)) ? GitRetryReason.Network : GitRetryReason.None;
    }

    public static string Describe(GitRetryReason reason, int attempt, int attempts) => reason == GitRetryReason.LockBusy
        ? $"Another git process is using the repository, trying again ({attempt} of {attempts})…"
        : $"Connection problem, trying again ({attempt} of {attempts})…";

    private static bool IsLockBusy(string? command, string stderr) =>
        (stderr.Contains("File exists", StringComparison.OrdinalIgnoreCase)
            && (stderr.Contains(".lock", StringComparison.OrdinalIgnoreCase) || stderr.Contains("could not lock", StringComparison.OrdinalIgnoreCase)))
        || stderr.Contains("Another git process seems to be running", StringComparison.OrdinalIgnoreCase)
        || (command is "fetch" && ContainsAny(stderr, FetchRefRaceSigns));

    private static bool ContainsAny(string text, string[] signs) => signs.Any(sign => text.Contains(sign, StringComparison.OrdinalIgnoreCase));
}
