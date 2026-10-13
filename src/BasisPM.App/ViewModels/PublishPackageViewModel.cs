using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.Core;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public interface IPublishHost
{
    Task<string?> GetTokenAsync();
    void RememberToken(string token);
    Task<GitHubUser?> GetUserAsync(string token);
    Task<IReadOnlyList<PackageIssue>> CheckAsync(string packageRoot, CancellationToken ct);
}

public sealed record PublishDraft(PublishRequest Request, string Token, GitHubUser User, bool CreateRepository, string RepositoryName, bool PrivateRepository,
    bool SubmitToRegistry, string Category, IReadOnlyList<string> Tags);

public sealed class PublishIssueItem
{
    public PublishIssueItem(PackageIssue issue) => Issue = issue;

    public PackageIssue Issue { get; }
    public string Message => Issue.Message;
    public string Location => Issue.Location;
    public bool HasLocation => Issue.Location.Length > 0;
    public bool IsError => Issue.Severity == IssueSeverity.Error;
    public bool IsWarning => Issue.Severity == IssueSeverity.Warning;
    public string SeverityLabel => L.Tr(IsError ? "dialog.publish.severity.error" : IsWarning ? "dialog.publish.severity.warning" : "dialog.publish.severity.info");
}

public sealed class PublishPackageViewModel : ObservableObject
{
    private readonly IPublishHost _host;
    private readonly IReadOnlySet<string> _tags;
    private string _version;
    private string _notes;
    private bool _createRelease = true;
    private string _repositoryName;
    private bool _privateRepository;
    private bool _submitToRegistry;
    private string _category;
    private string _tagsText = "";
    private bool _isChecking;
    private bool _isSigningIn;
    private string _errorText = "";

    public PublishPackageViewModel(IPublishHost host, PublishPlan plan, IEnumerable<string> existingTags, bool inRegistry, IReadOnlyList<string> categories)
    {
        _host = host;
        Plan = plan;
        _tags = new HashSet<string>(existingTags, StringComparer.Ordinal);
        InRegistry = inRegistry;
        Categories = categories;
        _category = categories.Contains("Misc") ? "Misc" : categories.FirstOrDefault() ?? "Misc";
        _version = plan.SuggestedVersion;
        _notes = PackagePublishService.SuggestNotes(plan.Commits);
        _repositoryName = PackageScaffoldService.Slug(plan.DisplayName) is { Length: > 0 } slug ? slug : plan.PackageId;
        Commits = plan.Commits.Select(c => $"{c.ShortSha}  {c.Subject}").ToList();
        PatchCommand = new RelayCommand(() => Bump(VersionBump.Patch));
        MinorCommand = new RelayCommand(() => Bump(VersionBump.Minor));
        MajorCommand = new RelayCommand(() => Bump(VersionBump.Major));
    }

    public PublishPlan Plan { get; }
    public Func<Task<string?>>? AskForToken { get; set; }
    public RelayCommand PatchCommand { get; }
    public RelayCommand MinorCommand { get; }
    public RelayCommand MajorCommand { get; }
    public ObservableCollection<PublishIssueItem> Issues { get; } = new();
    public IReadOnlyList<string> Commits { get; }
    public IReadOnlyList<string> Categories { get; }
    public bool InRegistry { get; }
    public bool CanSubmitToRegistry => !InRegistry;

    public string Title => L.Tr("dialog.publish.title", Plan.DisplayName);
    public string CurrentVersionText => Plan.LastTag is null
        ? L.Tr("dialog.publish.currentUntagged", Plan.CurrentVersion)
        : L.Tr("dialog.publish.current", Plan.CurrentVersion, Plan.LastTag);
    public string CommitsText => Plan.LastTag is null
        ? L.Tr("dialog.publish.commitsAll", Plan.Commits.Count)
        : L.Tr("dialog.publish.commitsSince", Plan.Commits.Count, Plan.LastTag);
    public bool HasCommits => Commits.Count > 0;
    public string ChangedFilesText => L.Tr("dialog.publish.changedFiles", Plan.ChangedFiles);
    public bool HasChangedFiles => Plan.ChangedFiles > 0;
    public bool NeedsRepository => !Plan.HasRemote;
    public bool IsDetached => Plan.Branch is null;
    public string BranchText => Plan.Branch is null || Plan.Origin is null ? "" : L.Tr("dialog.publish.branch", Plan.Branch, Plan.Origin.Slug);
    public bool HasBranchText => BranchText.Length > 0;

    public string Version
    {
        get => _version;
        set { if (SetField(ref _version, (value ?? "").Trim())) { OnPropertyChanged(nameof(VersionError)); OnPropertyChanged(nameof(HasVersionError)); OnPropertyChanged(nameof(TagPreview)); OnPropertyChanged(nameof(CanPublish)); } }
    }

    public string TagPreview => L.Tr("dialog.publish.tagPreview", Plan.TagFor(_version));

    public string VersionError
    {
        get
        {
            if (!PackagePublishService.IsValidVersion(_version)) return L.Tr("dialog.publish.error.version", _version);
            if (_tags.Contains(Plan.TagFor(_version))) return L.Tr("dialog.publish.error.tagExists", Plan.TagFor(_version));
            if (Plan.LastTag is { } last && SemVer.TryParseTag(last, out var lastVersion) && SemVer.TryParse(_version, out var next) && next.CompareTo(lastVersion) <= 0)
                return L.Tr("dialog.publish.error.notNewer", last);
            return "";
        }
    }

    public bool HasVersionError => VersionError.Length > 0;
    public string Notes { get => _notes; set => SetField(ref _notes, value); }
    public bool CreateRelease { get => _createRelease; set => SetField(ref _createRelease, value); }
    public string RepositoryName { get => _repositoryName; set => SetField(ref _repositoryName, (value ?? "").Trim()); }
    public bool PrivateRepository { get => _privateRepository; set => SetField(ref _privateRepository, value); }
    public bool SubmitToRegistry { get => _submitToRegistry; set => SetField(ref _submitToRegistry, value); }
    public string Category { get => _category; set { if (value is not null) SetField(ref _category, value); } }
    public string TagsText { get => _tagsText; set => SetField(ref _tagsText, value); }

    public bool IsChecking { get => _isChecking; private set { if (SetField(ref _isChecking, value)) OnPropertyChanged(nameof(CanPublish)); } }
    public bool HasIssues => Issues.Count > 0;
    public bool HasBlockingIssues => Issues.Any(i => i.IsError);
    public string ChecksText => IsChecking ? L.Tr("dialog.publish.checking")
        : HasBlockingIssues ? L.Tr("dialog.publish.checksBlocked", Issues.Count(i => i.IsError))
        : HasIssues ? L.Tr("dialog.publish.checksWarnings", Issues.Count)
        : L.Tr("dialog.publish.checksPassed");

    public bool IsSigningIn { get => _isSigningIn; private set { if (SetField(ref _isSigningIn, value)) { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(CanPublish)); } } }
    public bool IsIdle => !_isSigningIn;
    public bool CanPublish => !_isSigningIn && !_isChecking && !IsDetached && !HasVersionError && !HasBlockingIssues;
    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;

    private void Bump(VersionBump bump)
    {
        var from = Plan.LastTag is { } last && SemVer.TryParseTag(last, out var tagged) && (!SemVer.TryParse(Plan.CurrentVersion, out var current) || tagged.CompareTo(current) > 0)
            ? $"{tagged.Major}.{tagged.Minor}.{tagged.Patch}"
            : Plan.CurrentVersion;
        if (PackagePublishService.NextVersion(from, bump) is { } next) Version = next;
    }

    public async Task CheckAsync()
    {
        IsChecking = true;
        OnPropertyChanged(nameof(ChecksText));
        try
        {
            var issues = await _host.CheckAsync(Plan.PackageRoot, CancellationToken.None);
            Issues.Clear();
            foreach (var issue in issues.OrderByDescending(i => i.Severity)) Issues.Add(new PublishIssueItem(issue));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Checking {Plan.PackageId} before publishing", ex);
            Issues.Clear();
            Issues.Add(new PublishIssueItem(new PackageIssue("check-failed", IssueSeverity.Warning, Plan.PackageId, L.Tr("dialog.publish.checkFailed", ex.Message))));
        }
        finally
        {
            IsChecking = false;
            OnPropertyChanged(nameof(HasIssues));
            OnPropertyChanged(nameof(HasBlockingIssues));
            OnPropertyChanged(nameof(ChecksText));
            OnPropertyChanged(nameof(CanPublish));
        }
    }

    public async Task<PublishDraft?> ConfirmAsync()
    {
        if (!CanPublish) return null;
        if (NeedsRepository && string.IsNullOrWhiteSpace(_repositoryName))
        {
            ErrorText = L.Tr("dialog.publish.error.repositoryName");
            return null;
        }
        IsSigningIn = true;
        ErrorText = "";
        try
        {
            var (token, user, error) = await GitHubSignIn.SignInAsync(_host, AskForToken);
            if (token is null || user is null)
            {
                ErrorText = error;
                return null;
            }
            var tags = _tagsText.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return new PublishDraft(new PublishRequest(_version, _notes ?? "", _createRelease), token, user, NeedsRepository, _repositoryName, _privateRepository,
                _submitToRegistry && !InRegistry, _category, tags);
        }
        finally { IsSigningIn = false; }
    }
}

public static class GitHubSignIn
{
    public static async Task<(string? Token, GitHubUser? User, string Error)> SignInAsync(IPublishHost host, Func<Task<string?>>? askForToken)
    {
        try
        {
            var token = await host.GetTokenAsync();
            if (string.IsNullOrEmpty(token) && askForToken is not null && await askForToken() is { Length: > 0 } entered)
            {
                host.RememberToken(entered.Trim());
                token = await host.GetTokenAsync();
            }
            if (string.IsNullOrEmpty(token)) return (null, null, L.Tr("develop.status.signInFirst"));
            var user = await host.GetUserAsync(token);
            return user is null ? (null, null, L.Tr("develop.status.loginUnverified")) : (token, user, "");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Signing in to GitHub", ex);
            return (null, null, L.Tr("dialog.publish.error.signIn", ex.Message));
        }
    }
}
