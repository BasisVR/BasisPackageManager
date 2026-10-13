using System.Collections.ObjectModel;
using System.Globalization;
using BasisPM.App.Localization;
using BasisPM.App.Services;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed record ReleaseNoteItem(string Title, string Date, string Body)
{
    public bool HasBody => Body.Length > 0;
    public bool HasDate => Date.Length > 0;
}

public sealed record CommitItem(string ShortSha, string Subject, string Detail);

public sealed class PackageUpdateViewModel : ObservableObject
{
    private const int NoteLimit = 4000;

    private readonly Func<CancellationToken, Task<PackageChanges>> _load;
    private readonly PackageUpdateInfo _info;
    private bool _isLoading = true;
    private string _errorText = "";
    private string? _compareUrl;
    private bool _moreCommits;
    private RelayCommand? _openCompareCommand;

    public PackageUpdateViewModel(string displayName, PackageUpdateInfo info, bool hasLocalWork, Func<CancellationToken, Task<PackageChanges>> load)
    {
        DisplayName = displayName;
        _info = info;
        HasLocalWork = hasLocalWork;
        _load = load;
    }

    public string DisplayName { get; }
    public bool HasLocalWork { get; }
    public string Title => L.Tr("dialog.packageUpdate.title", DisplayName);
    public string FromText => _info.InstalledRef is { Length: > 0 } installed ? installed : PackageUpdateInfo.ShortCommit(_info.InstalledCommit);
    public string ToText => _info.Kind == PackageUpdateKind.Release ? _info.TargetRef ?? "" : $"{_info.TargetRef ?? L.Tr("packages.version.defaultBranch")} @ {PackageUpdateInfo.ShortCommit(_info.TargetCommit)}";
    public string Subtitle => L.Tr("dialog.packageUpdate.subtitle", FromText, ToText);
    public bool HasSkipped => _info.SkippedRef is not null && _info.SkippedProblem is not null;
    public string SkippedText => _info.SkippedProblem is { } problem ? L.Tr("packages.update.skipped", _info.SkippedRef ?? "", PackagesViewModel.DescribeProblem(problem)) : "";

    public ObservableCollection<ReleaseNoteItem> Releases { get; } = new();
    public ObservableCollection<CommitItem> Commits { get; } = new();
    public bool HasReleases => Releases.Count > 0;
    public bool HasCommits => Commits.Count > 0;
    public bool HasNothing => !_isLoading && !HasReleases && !HasCommits && !HasError;
    public bool IsLoading { get => _isLoading; private set { if (SetField(ref _isLoading, value)) OnPropertyChanged(nameof(HasNothing)); } }
    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) { OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(HasNothing)); } } }
    public bool HasError => _errorText.Length > 0;
    public bool MoreCommits { get => _moreCommits; private set => SetField(ref _moreCommits, value); }
    public string MoreCommitsText => L.Tr("dialog.packageUpdate.moreCommits");
    public bool HasCompareUrl => !string.IsNullOrWhiteSpace(_compareUrl);
    public RelayCommand OpenCompareCommand => _openCompareCommand ??= new RelayCommand(() => { if (_compareUrl is { } url) ExternalLink.Open(url); });

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            var changes = await _load(ct);
            Releases.Clear();
            foreach (var release in changes.Releases)
            {
                var body = (release.Body ?? "").Replace("\r\n", "\n").Trim();
                if (body.Length > NoteLimit) body = body[..NoteLimit].TrimEnd() + "…";
                var title = string.IsNullOrWhiteSpace(release.Name) || release.Name == release.Tag ? release.Tag : $"{release.Tag}  {release.Name}";
                Releases.Add(new ReleaseNoteItem(title, release.Published?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) ?? "", body));
            }
            Commits.Clear();
            foreach (var commit in changes.Commits)
                Commits.Add(new CommitItem(commit.ShortSha, commit.Subject, $"{commit.Author}, {commit.Date.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}"));
            MoreCommits = changes.MoreCommits;
            _compareUrl = changes.CompareUrl;
            OnPropertyChanged(nameof(HasCompareUrl));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Loading the changes in {DisplayName}", ex);
            ErrorText = L.Tr("dialog.packageUpdate.loadFailed", ex.Message);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasReleases));
            OnPropertyChanged(nameof(HasCommits));
            OnPropertyChanged(nameof(HasNothing));
        }
    }
}
