using System.Text.Json;
using System.Text.Json.Serialization;

namespace BasisPM.Core.Models;

public sealed class UpmPackageJson
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("unity")] public string? Unity { get; set; }
    [JsonPropertyName("unityRelease")] public string? UnityRelease { get; set; }
    // SPDX license string or expression (e.g. "MIT", "MIT AND Unlicense"), as declared for UPM.
    [JsonPropertyName("license")] public string? License { get; set; }
    [JsonPropertyName("author"), JsonConverter(typeof(UpmAuthorConverter))] public UpmAuthor? Author { get; set; }
    [JsonPropertyName("dependencies"), JsonConverter(typeof(StringMapConverter))] public Dictionary<string, string>? Dependencies { get; set; }
    [JsonPropertyName("vpmDependencies"), JsonConverter(typeof(StringMapConverter))] public Dictionary<string, string>? VpmDependencies { get; set; }
    [JsonPropertyName("gitDependencies"), JsonConverter(typeof(StringMapConverter))] public Dictionary<string, string>? GitDependencies { get; set; }
    [JsonPropertyName("basis"), JsonConverter(typeof(BasisCompatibilityConverter))] public BasisCompatibility? Basis { get; set; }
}

public sealed class UpmAuthor
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("email"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Email { get; set; }
    [JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Url { get; set; }
}

public sealed class BasisCompatibility
{
    [JsonPropertyName("from"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? From { get; set; }
    [JsonPropertyName("before"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Before { get; set; }
    [JsonPropertyName("channels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<string>? Channels { get; set; }
}

public sealed class UpmAuthorConverter : JsonConverter<UpmAuthor?>
{
    public override UpmAuthor? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return ParsePerson(reader.GetString());
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        var author = new UpmAuthor();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            reader.Read();
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            if (string.Equals(name, "name", StringComparison.OrdinalIgnoreCase)) author.Name = value ?? "";
            else if (string.Equals(name, "email", StringComparison.OrdinalIgnoreCase)) author.Email = value;
            else if (string.Equals(name, "url", StringComparison.OrdinalIgnoreCase)) author.Url = value;
        }
        return author;
    }

    public override void Write(Utf8JsonWriter writer, UpmAuthor? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("name", value.Name);
        if (!string.IsNullOrWhiteSpace(value.Email)) writer.WriteString("email", value.Email);
        if (!string.IsNullOrWhiteSpace(value.Url)) writer.WriteString("url", value.Url);
        writer.WriteEndObject();
    }

    public static UpmAuthor? ParsePerson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var rest = text.Trim();
        string? email = null, url = null;
        var open = rest.IndexOf('<');
        var close = open >= 0 ? rest.IndexOf('>', open) : -1;
        if (close > open)
        {
            email = rest[(open + 1)..close].Trim();
            rest = (rest[..open] + rest[(close + 1)..]).Trim();
        }
        open = rest.IndexOf('(');
        close = open >= 0 ? rest.IndexOf(')', open) : -1;
        if (close > open)
        {
            url = rest[(open + 1)..close].Trim();
            rest = (rest[..open] + rest[(close + 1)..]).Trim();
        }
        return new UpmAuthor { Name = rest, Email = string.IsNullOrEmpty(email) ? null : email, Url = string.IsNullOrEmpty(url) ? null : url };
    }
}

public sealed class BasisCompatibilityConverter : JsonConverter<BasisCompatibility?>
{
    public override BasisCompatibility? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() is { Length: > 0 } from ? new BasisCompatibility { From = from.Trim() } : null;
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        var result = new BasisCompatibility();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "channels", StringComparison.OrdinalIgnoreCase))
            {
                result.Channels = ReadChannels(ref reader);
                continue;
            }
            var value = reader.TokenType == JsonTokenType.String ? reader.GetString()?.Trim() : null;
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
            if (string.Equals(name, "from", StringComparison.OrdinalIgnoreCase)) result.From = string.IsNullOrEmpty(value) ? null : value;
            else if (string.Equals(name, "before", StringComparison.OrdinalIgnoreCase)) result.Before = string.IsNullOrEmpty(value) ? null : value;
        }
        return result.From is null && result.Before is null && result.Channels is null ? null : result;
    }

    private static List<string>? ReadChannels(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.GetString() is { Length: > 0 } single && single.Trim().Length > 0 ? new List<string> { single.Trim() } : null;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject) reader.Skip();
            return null;
        }
        var channels = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString()?.Trim() is { Length: > 0 } channel) channels.Add(channel);
            else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
        }
        return channels.Count == 0 ? null : channels;
    }

    public override void Write(Utf8JsonWriter writer, BasisCompatibility? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        if (!string.IsNullOrWhiteSpace(value.From)) writer.WriteString("from", value.From);
        if (!string.IsNullOrWhiteSpace(value.Before)) writer.WriteString("before", value.Before);
        if (value.Channels is { Count: > 0 } channels)
        {
            writer.WriteStartArray("channels");
            foreach (var channel in channels) writer.WriteStringValue(channel);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

public sealed class StringMapConverter : JsonConverter<Dictionary<string, string>?>
{
    public override Dictionary<string, string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var key = reader.GetString();
            reader.Read();
            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }
            var value = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => reader.TryGetDouble(out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(key) && value is not null) map[key] = value;
        }
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartObject();
        foreach (var (key, text) in value) writer.WriteString(key, text);
        writer.WriteEndObject();
    }
}
