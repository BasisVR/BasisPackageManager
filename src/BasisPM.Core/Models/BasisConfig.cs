using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class BasisConfig
{
    [JsonPropertyName("$schema")] public string? Schema { get; set; }
    public string? ProductName { get; set; }
    public string? CompanyName { get; set; }
    public string? Identifier { get; set; }
    public string? Version { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
    public Dictionary<string, JsonElement>? Settings { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    [JsonIgnore] public bool HasIdentity => ProductName is not null || CompanyName is not null || Identifier is not null || Version is not null;
}

public sealed record BasisIdentity(string? ProductName, string? CompanyName, string? Version, IReadOnlyDictionary<string, string> Identifiers);

public sealed record BasisConfigDrift(string Field, string? Config, string? ProjectSettings);
