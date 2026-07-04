using System.Collections.ObjectModel;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

/// <summary>Edit a git-loaded package: mount it locally, submit a PR upstream, then swap back or keep it.</summary>
public sealed class DevelopViewModel : ObservableObject
{
    private readonly MountService _mount;
    private readonly ContributeService _contribute;
    private readonly GitHubAuthService _auth;
    private readonly GitHubApiService _api;
    private readonly GitService _git;
    private readonly MountRegistry _registry;
    private readonly MainWindowViewModel _shell;

    private BasisInstall? _install;
    private bool _isBusy;
    private bool _authOk;
    private string _authLogin = "";
    private string _patInput = "";

    public ObservableCollection<GitPackageRow> GitPackages { get; } = new();
    public ObservableCollection<MountedRow> Mounted { get; } = new();

    public string InstallName => _install?.DisplayName ?? L.Tr("develop.install.none");
    public bool HasInstall => _install is not null && _install.HasUnityProject;
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }

    public bool HasGitPackages => GitPackages.Count > 0;
    public bool NoGitPackages => HasInstall && GitPackages.Count == 0;
    public bool HasMounted => Mounted.Count > 0;

    public bool GitAvailable => _git.IsAvailable;
    public bool GitMissing => !_git.IsAvailable;
    public bool GhAvailable => _auth.GhAvailable;

    public bool AuthOk
    {
        get => _authOk;
        private set { if (SetField(ref _authOk, value)) { OnPropertyChanged(nameof(NotAuthed)); OnPropertyChanged(nameof(AuthLabel)); OnPropertyChanged(nameof(ShowPatEntry)); } }
    }
    public bool NotAuthed => !_authOk;
    public bool ShowPatEntry => !_authOk;
    public string AuthLabel => _authOk
        ? L.Tr("develop.auth.signedIn", _authLogin)
        : GhAvailable ? L.Tr("develop.auth.cliNotLoggedIn")
                      : L.Tr("develop.auth.notSignedIn");

    public string PatInput { get => _patInput; set => SetField(ref _patInput, value); }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallGitCommand { get; }
    public RelayCommand RecheckCommand { get; }
    public RelayCommand ApplyPatCommand { get; }
    public RelayCommand<GitPackageRow> MountCommand { get; }
    public RelayCommand<MountedRow> SubmitPrCommand { get; }
    public RelayCommand<MountedRow> SwapBackCommand { get; }
    public RelayCommand<MountedRow> OpenInUnityCommand { get; }

    public DevelopViewModel(MountService mount, ContributeService contribute, GitHubAuthService auth,
        GitHubApiService api, GitService git, MountRegistry registry, MainWindowViewModel shell)
    {
        _mount = mount; _contribute = contribute; _auth = auth; _api = api; _git = git; _registry = registry; _shell = shell;

        RefreshCommand = new RelayCommand(RefreshAsync);
        InstallGitCommand = new RelayCommand(() => OpenUrl("https://git-scm.com/downloads"));
        RecheckCommand = new RelayCommand(RefreshAsync);
        ApplyPatCommand = new RelayCommand(ApplyPatAsync);
        MountCommand = new RelayCommand<GitPackageRow>(MountAsync);
        SubmitPrCommand = new RelayCommand<MountedRow>(SubmitPrAsync);
        SwapBackCommand = new RelayCommand<MountedRow>(SwapBackAsync);
        OpenInUnityCommand = new RelayCommand<MountedRow>(row => { if (_install is not null) _ = _shell.OpenProjectInUnityAsync(_install); });
    }

    public void SetActiveInstall(BasisInstall install)
    {
        _install = install;
        OnPropertyChanged(nameof(InstallName));
        OnPropertyChanged(nameof(HasInstall));
        Refresh();
        _ = CheckAuthAsync();
    }

    public async Task RefreshAsync()
    {
        Refresh();
        await CheckAuthAsync();
    }

    private void Refresh()
    {
        foreach (var n in new[] { nameof(GitAvailable), nameof(GitMissing), nameof(GhAvailable) })
            OnPropertyChanged(n);

        GitPackages.Clear();
        Mounted.Clear();

        if (_install is not null && _install.HasUnityProject)
        {
            var mountRecords = _registry.ForInstall(_install.UnityProjectPath).ToList();
            foreach (var rec in mountRecords)
                Mounted.Add(new MountedRow(rec.PackageId, rec.FolderPath, rec.OriginalManifestValue));

            var mounted = new HashSet<string>(mountRecords.Select(m => m.PackageId), StringComparer.OrdinalIgnoreCase);
            foreach (var (id, value) in _install.Manifest.Dependencies)
            {
                if (mounted.Contains(id)) continue;
                var parsed = UpmGitUrl.Parse(value);
                if (parsed is null || !(parsed.IsGitHub || parsed.IsGitLab)) continue;
                GitPackages.Add(new GitPackageRow(id, value, parsed.Host, parsed.Slug, parsed.Path is not null));
            }
        }

        OnPropertyChanged(nameof(HasGitPackages));
        OnPropertyChanged(nameof(NoGitPackages));
        OnPropertyChanged(nameof(HasMounted));
    }

    private async Task CheckAuthAsync()
    {
        try
        {
            var token = await _auth.GetTokenAsync();
            if (string.IsNullOrEmpty(token)) { _authLogin = ""; AuthOk = false; return; }
            var user = await _api.GetUserAsync(token);
            _authLogin = user?.Login ?? "";
            AuthOk = user is not null;
        }
        catch { AuthOk = false; }
    }

    private async Task ApplyPatAsync()
    {
        _auth.SetPersonalAccessToken(PatInput);
        await CheckAuthAsync();
        if (AuthOk) { PatInput = ""; _shell.SetStatus(L.Tr("develop.status.signedInAs", _authLogin), StatusKind.Success); }
        else _shell.SetStatus(L.Tr("develop.status.tokenFailed"), StatusKind.Error);
    }

    private async Task MountAsync(GitPackageRow? row)
    {
        if (row is null || _install is null) return;
        if (!_git.IsAvailable) { _shell.SetStatus(L.Tr("develop.status.gitRequiredMount"), StatusKind.Error); return; }

        // If Packages/<id> already exists (already mounted, a leftover, or a local copy), ask before replacing it.
        var dest = Path.Combine(_install.UnityProjectPath, "Packages", row.Id);
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            var mounted = _registry.Find(_install.UnityProjectPath, row.Id) is not null;
            var msg = mounted
                ? L.Tr("develop.confirm.alreadyMounted", row.Id)
                : L.Tr("develop.confirm.packageExists", row.Id);
            if (!await Dialogs.ConfirmAsync(L.Tr("develop.confirm.packageThereTitle"), msg))
            {
                _shell.SetStatus(L.Tr("develop.status.mountCancelled", row.Id), StatusKind.Info);
                return;
            }
            try { TryForceDelete(dest); }
            catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.clearFailed", row.Id, ex.Message), StatusKind.Error); return; }
        }

        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.mounting", row.Id));
        try
        {
            var result = await _mount.MountAsync(_install, row.Id, row.GitUrl, line => Dispatcher.UIThread.Post(() => _shell.SetStatus(line)));
            if (result.Ok) { _shell.SetStatus(L.Tr("develop.status.mounted", row.Id), StatusKind.Success); Refresh(); }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.mountFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.mountError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    private async Task SwapBackAsync(MountedRow? row)
    {
        if (row is null || _install is null) return;
        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.swapping", row.Id));
        try
        {
            var result = await _mount.SwapBackAsync(_install, row.Id);
            if (result.Ok) { _shell.SetStatus(L.Tr("develop.status.swappedBack", row.Id), StatusKind.Success); Refresh(); }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.swapBackFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.swapBackError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    private async Task SubmitPrAsync(MountedRow? row)
    {
        if (row is null) return;
        if (!_git.IsAvailable) { _shell.SetStatus(L.Tr("develop.status.gitRequired"), StatusKind.Error); return; }

        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) { _shell.SetStatus(L.Tr("develop.status.signInFirst"), StatusKind.Error); return; }
        var user = await _api.GetUserAsync(token);
        if (user is null) { _shell.SetStatus(L.Tr("develop.status.loginUnverified"), StatusKind.Error); return; }

        var draft = await Dialogs.SubmitPrAsync(row.Id);
        if (draft is null) return;

        IsBusy = true;
        _shell.SetStatus(L.Tr("develop.status.submittingPr", row.Id));
        try
        {
            var result = await _contribute.SubmitPrAsync(row.FolderPath, token, user, draft, line => Dispatcher.UIThread.Post(() => _shell.SetStatus(line)));
            if (result.Ok)
            {
                _shell.SetStatus(L.Tr("develop.status.prOpened", result.Forked ? L.Tr("develop.status.viaFork") : "", result.PrUrl), StatusKind.Success);
                if (!string.IsNullOrEmpty(result.PrUrl)) OpenUrl(result.PrUrl!);
            }
            else _shell.SetStatus(result.Error ?? L.Tr("develop.status.prFailed"), StatusKind.Error);
        }
        catch (Exception ex) { _shell.SetStatus(L.Tr("develop.status.prError", ex.Message), StatusKind.Error); }
        finally { IsBusy = false; }
    }

    // git marks objects under .git read-only; clear attributes before deleting the clone.
    private static void TryForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(path, recursive: true);
    }

    private static void OpenUrl(string url) => ExternalLink.Open(url);
}

public sealed record GitPackageRow(string Id, string GitUrl, string Host, string Slug, bool InSubfolder);

public sealed record MountedRow(string Id, string FolderPath, string OriginalManifestValue);
