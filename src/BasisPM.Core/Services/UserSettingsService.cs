using System.Text.Json;
using BasisPM.Core.Models;

namespace BasisPM.Core.Services;

public sealed class UserSettingsService(string? overridePath = null)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public string SettingsPath { get; } = overridePath ?? Path.Combine(AppDataPaths.Root, AppDataPaths.SettingsFileName);

    // Synchronous load for the one place that needs settings before the UI exists: applying the
    // saved UI language at startup (avoids a flash of English before the async load completes).
    public UserSettings Load()
    {
        Gate.Wait();
        try
        {
            if (!File.Exists(SettingsPath)) return new UserSettings();
            using var fs = File.OpenRead(SettingsPath);
            return JsonSerializer.Deserialize<UserSettings>(fs, JsonOpts) ?? new UserSettings();
        }
        catch (Exception ex) { return Unreadable($"Loading settings synchronously from {SettingsPath}", ex); }
        finally { Gate.Release(); }
    }

    public async Task<UserSettings> LoadAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(SettingsPath)) return new UserSettings();
            await using var fs = File.OpenRead(SettingsPath);
            return await JsonSerializer.DeserializeAsync<UserSettings>(fs, JsonOpts, ct).ConfigureAwait(false) ?? new UserSettings();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Unreadable($"Loading settings asynchronously from {SettingsPath}", ex); }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try { await AtomicFile.WriteAsync(SettingsPath, stream => JsonSerializer.SerializeAsync(stream, settings, JsonOpts, ct), ct).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private UserSettings Unreadable(string context, Exception ex)
    {
        DiagnosticLog.Write(context, ex);
        if (ex is JsonException) AtomicFile.KeepUnreadableCopy(SettingsPath);
        return new UserSettings();
    }
}
