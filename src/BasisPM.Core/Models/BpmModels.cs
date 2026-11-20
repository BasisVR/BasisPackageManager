using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class BpmManifest
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public BpmSettings? Settings { get; set; }
    public Dictionary<string, BpmCheckout>? Checkouts { get; set; }
    public Dictionary<string, BpmManifestPackage> Packages { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsEmpty => Packages.Count == 0 && Checkouts is not { Count: > 0 } && Settings is null && Extra is not { Count: > 0 };
}

public sealed class BpmSettings
{
    public string? DefaultCheckout { get; set; }
    public string? Template { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public enum BpmHeld { Pinned, WorkingTree, Inline }

public sealed class BpmCheckout
{
    public string? Url { get; set; }
    public string? Path { get; set; }
    public string? Commit { get; set; }
    public string? Branch { get; set; }
    public string? Held { get; set; }
    public string? Repository { get; set; }
    public string? Packages { get; set; }
    public List<string>? Sparse { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore]
    public BpmHeld Kind => (Held ?? "").Trim().ToLowerInvariant() switch
    {
        "pinned" => BpmHeld.Pinned,
        "working-tree" or "worktree" or "working tree" => BpmHeld.WorkingTree,
        "inline" => BpmHeld.Inline,
        _ => !string.IsNullOrWhiteSpace(Commit) ? BpmHeld.Pinned : !string.IsNullOrWhiteSpace(Branch) ? BpmHeld.WorkingTree : BpmHeld.Inline,
    };

    [JsonIgnore] public string PackagesFolder => string.IsNullOrWhiteSpace(Packages) ? "Basis/Packages" : Packages.Replace('\\', '/').Trim('/');
}

public sealed class BpmManifestPackage
{
    public string Version { get; set; } = "";
    public string? Enabled { get; set; }
    public string? CopiedFrom { get; set; }
    public string? ForkOf { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsEnabled => !string.Equals(Enabled?.Trim(), "no", StringComparison.OrdinalIgnoreCase);
}

public sealed class BpmLock
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public Dictionary<string, BpmLockCheckout>? Checkouts { get; set; }
    public Dictionary<string, BpmLockPackage> Packages { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool IsEmpty => Packages.Count == 0 && Checkouts is not { Count: > 0 } && Extra is not { Count: > 0 };
}

public sealed class BpmLockCheckout
{
    public string? Url { get; set; }
    public string? Commit { get; set; }
    public string? Branch { get; set; }
    public string Held { get; set; } = "pinned";
    public string? Path { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BpmLockPackage
{
    public string Version { get; set; } = "";
    public string? Requested { get; set; }
    public string? PackageVersion { get; set; }
    public string? ForkOf { get; set; }
    public string? Folder { get; set; }
    public int Depth { get; set; }
    public Dictionary<string, string>? Dependencies { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public enum BpmVersionKind { Invalid, Registry, Git, File, Checkout }

public sealed record BpmVersion(BpmVersionKind Kind, string Value, string? Url = null, string? SubPath = null, string? Ref = null)
{
    public static BpmVersion Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return new(BpmVersionKind.Invalid, raw);
        if (raw.StartsWith("checkout:", StringComparison.OrdinalIgnoreCase)) return raw.Length > 9 ? new(BpmVersionKind.Checkout, raw[9..].Trim()) : new(BpmVersionKind.Invalid, raw);
        if (raw.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return raw.Length > 5 ? new(BpmVersionKind.File, raw[5..].Trim()) : new(BpmVersionKind.Invalid, raw);
        if (!IsGitLike(raw)) return new(BpmVersionKind.Registry, raw);
        string? gitRef = null, path = null;
        var hash = raw.LastIndexOf('#');
        var url = raw;
        if (hash >= 0) { gitRef = Blank(url[(hash + 1)..]); url = url[..hash]; }
        var q = url.IndexOf('?');
        if (q >= 0)
        {
            foreach (var pair in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("path", StringComparison.OrdinalIgnoreCase)) path = Blank(Uri.UnescapeDataString(kv[1]).Trim('/'));
            }
            url = url[..q];
        }
        return new(BpmVersionKind.Git, raw, url, path, gitRef);
    }

    public static string Git(string url, string? subPath, string? gitRef) =>
        url + (string.IsNullOrWhiteSpace(subPath) ? "" : "?path=" + subPath.Trim('/')) + (string.IsNullOrWhiteSpace(gitRef) ? "" : "#" + gitRef.Trim());

    public static bool IsCommitHash(string? value) => value is { Length: >= 7 and <= 64 } && value.All(Uri.IsHexDigit);

    private static bool IsGitLike(string raw)
    {
        var bare = raw.Split('#', '?')[0];
        return bare.Contains("://", StringComparison.Ordinal) || bare.StartsWith("git@", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("git+", StringComparison.OrdinalIgnoreCase)
            || bare.EndsWith(".git", StringComparison.OrdinalIgnoreCase) || System.IO.Path.IsPathRooted(bare);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class BpmLocalState
{
    public const int CurrentFormat = 1;

    public int Format { get; set; } = CurrentFormat;
    public Dictionary<string, BpmLocalProject> Projects { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class BpmLocalProject
{
    public Dictionary<string, string>? Overrides { get; set; }
    public Dictionary<string, string>? Mounted { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
}
