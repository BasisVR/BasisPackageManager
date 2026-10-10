using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class ServerPackageManifest
{
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string> Dependencies { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed record ServerPackageModule(string Path, string Assembly, IReadOnlyList<string> Excludes);

public sealed record ServerPackageBinary(string Path, string Assembly, IReadOnlyList<string> Platforms);

public sealed record ServerPackageDeclaration(
    string Id,
    string Version,
    string DisplayName,
    string Description,
    IReadOnlyList<ServerPackageModule> Modules,
    IReadOnlyDictionary<string, string> NuGet,
    IReadOnlyList<string> Problems)
{
    public IReadOnlyList<ServerPackageBinary> References { get; init; } = Array.Empty<ServerPackageBinary>();
    public IReadOnlyList<ServerPackageBinary> Natives { get; init; } = Array.Empty<ServerPackageBinary>();
    public bool IsValid => Problems.Count == 0 && Modules.Count > 0;
}

public sealed record ServerPackageLockEntry(
    string Id,
    string Version,
    string Source,
    string? Url,
    string? Ref,
    string? Commit,
    string? SubPath,
    string? LocalPath,
    IReadOnlyList<ServerPackageModule> Modules,
    IReadOnlyDictionary<string, string> NuGet)
{
    public IReadOnlyList<ServerPackageBinary> References { get; init; } = Array.Empty<ServerPackageBinary>();
    public IReadOnlyList<ServerPackageBinary> Natives { get; init; } = Array.Empty<ServerPackageBinary>();
}

public enum ServerPackageStatus { Ready, NotRestored, Changed, Missing, Invalid }

public sealed record ServerPackageInfo(
    string Id,
    string DisplayName,
    string Version,
    string Source,
    string? Commit,
    string? Folder,
    string? LinkedFolder,
    ServerPackageStatus Status,
    IReadOnlyList<ServerPackageModule> Modules,
    string? Detail)
{
    public IReadOnlyList<ServerPackageBinary> References { get; init; } = Array.Empty<ServerPackageBinary>();
    public IReadOnlyList<ServerPackageBinary> Natives { get; init; } = Array.Empty<ServerPackageBinary>();
    public bool IsLinked => LinkedFolder is not null;
    public bool IsLocal => Source.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    public string ShortCommit => Commit is { Length: > 7 } commit ? commit[..7] : Commit ?? "";
    public string Assemblies => string.Join(", ", Modules.Select(m => m.Assembly).Distinct(StringComparer.Ordinal));
}

public sealed record ServerPackageSource(string Source, string? ExpectedId, CatalogPackageVersion? Entry, string? Error);

public sealed record ServerPackageResult(bool Ok, string? Id, string Message)
{
    public static ServerPackageResult Success(string id, string message) => new(true, id, message);
    public static ServerPackageResult Fail(string message, string? id = null) => new(false, id, message);
}
