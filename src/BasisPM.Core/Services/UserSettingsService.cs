using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class UserSettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string SettingsPath { get; }

    public UserSettingsService(string? overridePath = null)
    {
        SettingsPath = overridePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BasisPM",
            "settings.json");
    }

    public async Task<UserSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(SettingsPath)) return new UserSettings();
        try
        {
            await using var fs = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<UserSettings>(fs, JsonOpts, ct).ConfigureAwait(false)
                   ?? new UserSettings();
        }
        catch
        {
            return new UserSettings();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var fs = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(fs, settings, JsonOpts, ct).ConfigureAwait(false);
    }
}
