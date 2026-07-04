using System.Reflection;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace BasisPM.App.Services;

/// <summary>
/// Wraps Velopack's <see cref="UpdateManager"/> against this repo's GitHub Releases.
/// Only functions when the app was installed via a Velopack package; running from source
/// (<c>dotnet run</c>) reports <see cref="IsSupported"/> = false and never surfaces update UI.
/// </summary>
public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/BasisVR/BasisPackageManager";

    private UpdateManager _mgr;
    private bool _prerelease;

    public UpdateService()
    {
        _mgr = Build(false);
    }

    // accessToken null = public repo; the prerelease flag picks the stable vs prerelease channel.
    private static UpdateManager Build(bool prerelease) =>
        new(new GithubSource(RepoUrl, null, prerelease, null));

    /// <summary>Whether the updater is currently following the prerelease channel.</summary>
    public bool Prerelease => _prerelease;

    /// <summary>Switch update channel: false = stable releases only, true = include prereleases.</summary>
    public void SetPrerelease(bool prerelease)
    {
        if (prerelease == _prerelease) return;
        _prerelease = prerelease;
        _mgr = Build(prerelease);
    }

    /// <summary>True only when launched from a Velopack install (i.e. self-update is possible).</summary>
    public bool IsSupported => _mgr.IsInstalled;

    /// <summary>The running version — Velopack's installed version, or the assembly version in dev.</summary>
    public string CurrentVersion =>
        _mgr.IsInstalled && _mgr.CurrentVersion is { } v ? v.ToString() : AssemblyVersion();

    /// <summary>Returns update info when a newer stable release exists, otherwise null.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null;
        return await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the update (reporting 0-100 progress) then swaps files and relaunches the app.
    /// Does not return on success — the process is restarted onto the new version.
    /// </summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info, Action<int> progress, CancellationToken ct = default)
    {
        await _mgr.DownloadUpdatesAsync(info, progress, ct).ConfigureAwait(false);
        _mgr.ApplyUpdatesAndRestart(info.TargetFullRelease);
    }

    /// <summary>Creates a Desktop shortcut for the installed app (Windows + Velopack-installed only).</summary>
    public void CreateDesktopShortcut()
    {
        if (!OperatingSystem.IsWindows() || !_mgr.IsInstalled || !VelopackLocator.IsCurrentSet) return;
#pragma warning disable CS0618 // Shortcuts is auto-managed for Desktop/StartMenuRoot; we add a Desktop icon on demand.
        new Velopack.Windows.Shortcuts(VelopackLocator.Current)
            .CreateShortcutForThisExe(Velopack.Windows.ShortcutLocation.Desktop);
#pragma warning restore CS0618
    }

    private static string AssemblyVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
