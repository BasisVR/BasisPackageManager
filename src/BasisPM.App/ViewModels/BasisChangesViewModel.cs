using System.Collections.ObjectModel;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public interface IBasisChangesHost
{
    string Repository { get; }
    Task<BasisContributeScan> ScanAsync(Action<string> progress, CancellationToken ct);
    Task<string?> GetProjectRemoteAsync(string repoRoot);
    Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(BasisContributeScan scan, BasisChange change, CancellationToken ct);
    Task<IReadOnlyList<string>> ListBasisBranchesAsync();
    Task<string?> GetTokenAsync();
    void RememberToken(string token);
    Task<GitHubUser?> GetUserAsync(string token);
    Task<BasisContributeResult> SubmitAsync(BasisContributeScan scan, IReadOnlyList<string> paths, BasisPullRequestDraft draft, string token, GitHubUser user, Action<string> progress, CancellationToken ct);
    void OpenUrl(string url);
    Guid BeginActivity(string title, string project);
    void ReportActivity(Guid id, string detail);
    void EndActivity(Guid id);
    void SetStatus(string message, StatusKind kind, string project);
}

public sealed record DiffLineItem(string Text, BasisDiffLineKind Kind)
{
    public bool IsAdded => Kind == BasisDiffLineKind.Added;
    public bool IsRemoved => Kind == BasisDiffLineKind.Removed;
    public bool IsHunk => Kind == BasisDiffLineKind.Hunk;
    public bool IsMeta => Kind is BasisDiffLineKind.Header or BasisDiffLineKind.Note;
}

public sealed class BasisChangeFileItem : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;
    private bool _isSelected;

    public BasisChangeFileItem(BasisChangeGroupItem group, BasisChange change, BasisChange? meta, bool isChurn, Action changed)
    {
        Group = group;
        Change = change;
        Meta = meta;
        IsChurn = isChurn;
        _changed = changed;
        DisplayPath = group.Group.RelativePath(change);
        Paths = meta is null ? new[] { change.Path } : new[] { change.Path, meta.Path };
    }

    public BasisChangeGroupItem Group { get; }
    public BasisChange Change { get; }
    public BasisChange? Meta { get; }
    public IReadOnlyList<string> Paths { get; }
    public string DisplayPath { get; }
    public string FullPath => Change.ProjectPath;
    public bool HasMeta => Meta is not null;
    public bool IsChurn { get; }
    public bool IsAdded => Change.Kind == BasisChangeKind.Added;
    public bool IsDeleted => Change.Kind == BasisChangeKind.Deleted;
    public bool IsModified => Change.Kind == BasisChangeKind.Modified;
    public string KindLetter => Change.Kind switch { BasisChangeKind.Added => "A", BasisChangeKind.Deleted => "D", _ => "M" };
    public string KindLabel => L.Tr(Change.Kind switch
    {
        BasisChangeKind.Added => "basisChanges.kind.added",
        BasisChangeKind.Deleted => "basisChanges.kind.deleted",
        _ => "basisChanges.kind.modified",
    });

    public bool IsChecked
    {
        get => _isChecked;
        set { if (SetField(ref _isChecked, value)) _changed(); }
    }

    public bool IsSelected { get => _isSelected; internal set => SetField(ref _isSelected, value); }

    internal void SetChecked(bool value) => SetField(ref _isChecked, value, nameof(IsChecked));
}

public sealed class BasisChangeGroupItem : ObservableObject
{
    private readonly Action _changed;
    private readonly Action<BasisChangeGroupItem> _toggled;
    private bool _isExpanded;

    public BasisChangeGroupItem(BasisChangeGroup group, Action changed, Action<BasisChangeGroupItem> toggled)
    {
        Group = group;
        _changed = changed;
        _toggled = toggled;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public BasisChangeGroup Group { get; }
    public List<BasisChangeFileItem> Files { get; } = new();
    public RelayCommand ToggleCommand { get; }

    public string Name => Group.Area switch
    {
        BasisChangeArea.Package => Group.Name,
        BasisChangeArea.Project => L.Tr("basisChanges.group.project"),
        _ => L.Tr("basisChanges.group.repository"),
    };

    public string Subtitle
    {
        get
        {
            var count = Group.Changes.Count == 1 ? L.Tr("basisChanges.group.oneFile") : L.Tr("basisChanges.group.files", Group.Changes.Count);
            return Group.InBasis ? count : L.Tr("basisChanges.group.notInBasis", count);
        }
    }

    public bool IsPackage => Group.Area == BasisChangeArea.Package;

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (SetField(ref _isExpanded, value)) _toggled(this); }
    }

    public bool? IsChecked
    {
        get
        {
            var picked = Files.Count(f => f.IsChecked);
            return picked == 0 ? false : picked == Files.Count ? true : null;
        }
        set
        {
            var on = value != false;
            foreach (var file in Files) file.SetChecked(on);
            OnPropertyChanged();
            _changed();
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

public sealed class BasisChangesViewModel : ObservableObject
{
    private readonly IBasisChangesHost _host;
    private readonly string? _focusPackage;
    private BasisContributeScan? _scan;
    private CancellationTokenSource? _diffLoad;
    private BasisChangeFileItem? _selectedFile;
    private IReadOnlyList<DiffLineItem> _diffLines = Array.Empty<DiffLineItem>();
    private string _diffTitle = "";
    private string _diffHint = "";
    private bool _isLoading;
    private bool _isSubmitting;
    private bool _isDiffLoading;
    private string _statusText = "";
    private string _errorText = "";
    private string _messageText = "";
    private string _basedOnText = "";
    private string _remoteText = "";
    private string _resultText = "";
    private string _resultUrl = "";
    private string _compareUrl = "";
    private string _noticeText = "";
    private string _title = "";
    private string _body = "";
    private string _branch = "";
    private string? _targetBranch;
    private int _selectedCount;
    private bool _suppress;
    private Guid _activity;

    public BasisChangesViewModel(IBasisChangesHost host, string projectName, string? focusPackage = null)
    {
        _host = host;
        ProjectName = projectName;
        _focusPackage = string.IsNullOrWhiteSpace(focusPackage) ? null : focusPackage;
        _diffHint = L.Tr("basisChanges.diff.pick");
        RefreshCommand = new RelayCommand(LoadAsync, () => !IsBusy);
        SubmitCommand = new RelayCommand(SubmitAsync, () => CanSubmit);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => HasChanges && !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => HasChanges && !IsBusy);
        OpenResultCommand = new RelayCommand(() => { if (_resultUrl.Length > 0) _host.OpenUrl(_resultUrl); });
        OpenCompareCommand = new RelayCommand(() => { if (_compareUrl.Length > 0) _host.OpenUrl(_compareUrl); });
        SelectFileCommand = new RelayCommand<BasisChangeFileItem>(SelectFile);
    }

    public Func<Task<string?>>? AskForToken { get; set; }

    public string ProjectName { get; }
    public string Repository => _host.Repository;
    public ObservableCollection<BasisChangeGroupItem> Groups { get; } = new();
    public ObservableCollection<object> Rows { get; } = new();
    public ObservableCollection<string> TargetBranches { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand SubmitCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand OpenResultCommand { get; }
    public RelayCommand OpenCompareCommand { get; }
    public RelayCommand<BasisChangeFileItem> SelectFileCommand { get; }

    public BasisContributeScan? Scan => _scan;

    public bool IsLoading { get => _isLoading; private set { if (SetField(ref _isLoading, value)) RaiseState(); } }
    public bool IsSubmitting { get => _isSubmitting; private set { if (SetField(ref _isSubmitting, value)) RaiseState(); } }
    public bool IsBusy => _isLoading || _isSubmitting;
    public bool IsIdle => !IsBusy;
    public bool HasBase => _scan is { IsBlocked: false };
    public bool HasChanges => !_isLoading && _scan is { IsBlocked: false, ChangeCount: > 0 };
    public bool ShowMessage => !_isLoading && _messageText.Length > 0;

    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string MessageText { get => _messageText; private set { if (SetField(ref _messageText, value)) OnPropertyChanged(nameof(ShowMessage)); } }
    public string BasedOnText { get => _basedOnText; private set => SetField(ref _basedOnText, value); }
    public string RemoteText { get => _remoteText; private set => SetField(ref _remoteText, value); }

    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;
    public string ResultText { get => _resultText; private set { if (SetField(ref _resultText, value)) OnPropertyChanged(nameof(HasResult)); } }
    public bool HasResult => _resultText.Length > 0;
    public string ResultUrl => _resultUrl;
    public bool HasCompareUrl => _compareUrl.Length > 0;
    public string NoticeText { get => _noticeText; private set { if (SetField(ref _noticeText, value)) OnPropertyChanged(nameof(HasNotice)); } }
    public bool HasNotice => _noticeText.Length > 0;

    public BasisChangeFileItem? SelectedFile => _selectedFile;

    public void SelectFile(BasisChangeFileItem? file)
    {
        if (file is null) return;
        if (_selectedFile is not null) _selectedFile.IsSelected = false;
        _selectedFile = file;
        file.IsSelected = true;
        if (!file.Group.IsExpanded) file.Group.IsExpanded = true;
        OnPropertyChanged(nameof(SelectedFile));
        _ = LoadDiffAsync(file);
    }

    public IReadOnlyList<DiffLineItem> DiffLines { get => _diffLines; private set { if (SetField(ref _diffLines, value)) OnPropertyChanged(nameof(HasDiff)); } }
    public bool HasDiff => _diffLines.Count > 0;
    public string DiffTitle { get => _diffTitle; private set => SetField(ref _diffTitle, value); }
    public string DiffHint { get => _diffHint; private set => SetField(ref _diffHint, value); }
    public bool IsDiffLoading { get => _isDiffLoading; private set => SetField(ref _isDiffLoading, value); }

    public string Title { get => _title; set { if (SetField(ref _title, value)) RaiseState(); } }
    public string Body { get => _body; set => SetField(ref _body, value); }
    public string Branch { get => _branch; set { if (SetField(ref _branch, value)) RaiseState(); } }
    public string? TargetBranch { get => _targetBranch; set { if (SetField(ref _targetBranch, value)) RaiseState(); } }

    public int SelectedCount => _selectedCount;
    public string SelectionText => _selectedCount switch
    {
        0 => L.Tr("basisChanges.selectedNone"),
        1 => L.Tr("basisChanges.selectedOne"),
        _ => L.Tr("basisChanges.selected", _selectedCount),
    };

    public bool CanSubmit => !IsBusy && HasChanges && _selectedCount > 0 && _title.Trim().Length > 0 && _branch.Trim().Length > 0 && !string.IsNullOrWhiteSpace(_targetBranch);

    public IReadOnlyList<string> SelectedPaths() =>
        _scan is null ? Array.Empty<string>() : BasisContributeService.ExpandSelection(_scan, Groups.SelectMany(g => g.Files).Where(f => f.IsChecked).SelectMany(f => f.Paths)).Select(c => c.Path).ToList();

    public async Task LoadAsync()
    {
        if (IsBusy) return;
        IsLoading = true;
        ErrorText = "";
        ResultText = "";
        SetCompareUrl("");
        MessageText = "";
        NoticeText = "";
        StatusText = L.Tr("basisChanges.loading", ProjectName);
        var focused = _selectedFile?.Change.Path;
        var activity = _activity = _host.BeginActivity(StatusText, ProjectName);
        try
        {
            var scan = await _host.ScanAsync(line => Progress(activity, line), CancellationToken.None);
            _scan = scan;
            BuildGroups(scan);
            if (scan.IsBlocked)
            {
                MessageText = DescribeBlock(scan);
                return;
            }
            BasedOnText = DescribeBase(scan);
            var remote = await _host.GetProjectRemoteAsync(scan.RepoRoot);
            RemoteText = remote is null ? L.Tr("basisChanges.remoteNone", Repository)
                : BasisUpdateService.IsSameRepository(remote, $"https://github.com/{Repository}.git") ? L.Tr("basisChanges.remoteIsBasis", Repository)
                : L.Tr("basisChanges.remote", remote, Repository);
            if (scan.ChangeCount == 0)
            {
                MessageText = L.Tr("basisChanges.empty", Short(scan.Base!.Sha));
                return;
            }
            if (_focusPackage is not null && scan.FindPackage(_focusPackage) is null)
                NoticeText = L.Tr("basisChanges.focusUnchanged", _focusPackage);
            if (_title.Length == 0) Title = _focusPackage is not null ? L.Tr("basisChanges.defaultTitlePackage", _focusPackage) : L.Tr("basisChanges.defaultTitle", ProjectName);
            if (_branch.Length == 0) Branch = BasisContributeService.SuggestBranch(_focusPackage ?? ProjectName, DateTimeOffset.Now);
            await LoadBranchesAsync(BasisContributeService.SuggestTarget(scan));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Comparing a project with Basis", ex);
            _scan = null;
            Groups.Clear();
            Rows.Clear();
            MessageText = L.Tr("basisChanges.block.failed", ex.Message);
        }
        finally
        {
            EndActivity(activity);
            IsLoading = false;
            StatusText = "";
            RaiseSelection();
        }
        var select = Groups.SelectMany(g => g.Files).FirstOrDefault(f => f.Change.Path == focused)
            ?? Groups.Where(g => g.IsExpanded).SelectMany(g => g.Files).FirstOrDefault(f => f.IsChecked)
            ?? Groups.SelectMany(g => g.Files).FirstOrDefault();
        if (select is not null) SelectFile(select);
    }

    private void BuildGroups(BasisContributeScan scan)
    {
        Groups.Clear();
        Rows.Clear();
        _selectedFile = null;
        OnPropertyChanged(nameof(SelectedFile));
        DiffLines = Array.Empty<DiffLineItem>();
        DiffTitle = "";
        DiffHint = L.Tr("basisChanges.diff.pick");
        var picked = new HashSet<string>(BasisContributeService.SuggestSelection(scan, _focusPackage), StringComparer.Ordinal);
        _suppress = true;
        foreach (var group in scan.Groups)
        {
            var item = new BasisChangeGroupItem(group, OnSelectionChanged, OnGroupToggled);
            var byPath = group.Changes.ToDictionary(c => c.Path, StringComparer.Ordinal);
            foreach (var change in group.Changes)
            {
                if (change.IsMeta && IsCompanion(change, byPath)) continue;
                var meta = !change.IsMeta && byPath.TryGetValue(change.Path + ".meta", out var m) ? m : null;
                var file = new BasisChangeFileItem(item, change, meta, BasisContributeService.IsEditorChurn(scan, change), OnSelectionChanged);
                file.SetChecked(picked.Contains(change.Path));
                item.Files.Add(file);
            }
            Groups.Add(item);
        }
        foreach (var item in Groups)
            item.IsExpanded = Groups.Count == 1 || (_focusPackage is null ? item.IsPackage && item.Group.InBasis : string.Equals(item.Group.Name, _focusPackage, StringComparison.OrdinalIgnoreCase) && item.IsPackage);
        _suppress = false;
        RebuildRows();
        RecountSelection();
    }

    private static bool IsCompanion(BasisChange meta, IReadOnlyDictionary<string, BasisChange> byPath)
    {
        var asset = meta.Path[..^5];
        if (byPath.ContainsKey(asset)) return true;
        return meta.Kind != BasisChangeKind.Modified && byPath.Keys.Any(p => p.StartsWith(asset + "/", StringComparison.Ordinal));
    }

    private void OnGroupToggled(BasisChangeGroupItem group)
    {
        if (!_suppress) RebuildRows();
    }

    private void RebuildRows()
    {
        Rows.Clear();
        foreach (var group in Groups)
        {
            Rows.Add(group);
            if (group.IsExpanded) foreach (var file in group.Files) Rows.Add(file);
        }
    }

    private void OnSelectionChanged()
    {
        if (_suppress) return;
        foreach (var group in Groups) group.Refresh();
        RecountSelection();
    }

    private void SetAll(bool on)
    {
        _suppress = true;
        foreach (var file in Groups.SelectMany(g => g.Files)) file.SetChecked(on);
        _suppress = false;
        OnSelectionChanged();
    }

    private void RecountSelection()
    {
        _selectedCount = SelectedPaths().Count;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        RaiseState();
    }

    private async Task LoadBranchesAsync(string suggested)
    {
        IReadOnlyList<string> branches;
        try { branches = await _host.ListBasisBranchesAsync(); }
        catch (Exception ex) { DiagnosticLog.Write("Listing the Basis branches", ex); branches = Array.Empty<string>(); }
        var current = _targetBranch;
        TargetBranches.Clear();
        foreach (var branch in branches.Append(suggested).Append(BasisInstallService.DefaultBranch).Distinct(StringComparer.Ordinal)) TargetBranches.Add(branch);
        TargetBranch = current is not null && TargetBranches.Contains(current) ? current : suggested;
    }

    private async Task LoadDiffAsync(BasisChangeFileItem file)
    {
        _diffLoad?.Cancel();
        var load = _diffLoad = new CancellationTokenSource();
        DiffTitle = file.FullPath;
        DiffLines = Array.Empty<DiffLineItem>();
        DiffHint = L.Tr("basisChanges.diff.loading");
        IsDiffLoading = true;
        try
        {
            if (_scan is null) return;
            var lines = await _host.GetDiffAsync(_scan, file.Change, load.Token);
            if (load.IsCancellationRequested) return;
            DiffLines = lines.Select(l => new DiffLineItem(l.Text.Replace("\t", "    "), l.Kind)).ToList();
            DiffHint = L.Tr("basisChanges.diff.pick");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Showing the Basis diff of {file.FullPath}", ex);
            if (!load.IsCancellationRequested) DiffHint = L.Tr("basisChanges.diff.failed", ex.Message);
        }
        finally { if (_diffLoad == load) IsDiffLoading = false; }
    }

    public async Task SubmitAsync()
    {
        if (_scan is null || !CanSubmit) return;
        IsSubmitting = true;
        ErrorText = "";
        ResultText = "";
        SetCompareUrl("");
        StatusText = L.Tr("basisChanges.status.signingIn");
        var activity = _activity = _host.BeginActivity(L.Tr("basisChanges.activity.submit", Repository), ProjectName);
        try
        {
            var token = await _host.GetTokenAsync();
            if (string.IsNullOrEmpty(token) && AskForToken is not null && await AskForToken() is { Length: > 0 } entered)
            {
                _host.RememberToken(entered.Trim());
                token = await _host.GetTokenAsync();
            }
            if (string.IsNullOrEmpty(token))
            {
                ErrorText = L.Tr("basisChanges.status.signIn");
                return;
            }
            var user = await _host.GetUserAsync(token);
            if (user is null)
            {
                ErrorText = L.Tr("basisChanges.status.loginFailed");
                return;
            }
            var draft = new BasisPullRequestDraft(_title.Trim(), string.IsNullOrWhiteSpace(_body) ? null : _body.Trim(), _branch.Trim(), _targetBranch!.Trim());
            var result = await _host.SubmitAsync(_scan, SelectedPaths(), draft, token, user, line => Progress(activity, line), CancellationToken.None);
            if (result.Ok && result.Url is { Length: > 0 } url)
            {
                _resultUrl = url;
                OnPropertyChanged(nameof(ResultUrl));
                ResultText = L.Tr(result.Updated ? "basisChanges.status.updated" : "basisChanges.status.opened", url);
                _host.SetStatus(ResultText, StatusKind.Success, ProjectName);
                _host.OpenUrl(url);
            }
            else
            {
                ErrorText = result.Error ?? L.Tr("basisChanges.status.failed");
                SetCompareUrl(result.CompareUrl ?? "");
                _host.SetStatus(ErrorText, StatusKind.Error, ProjectName);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Opening a pull request on Basis", ex);
            ErrorText = L.Tr("basisChanges.status.error", ex.Message);
            _host.SetStatus(ErrorText, StatusKind.Error, ProjectName);
        }
        finally
        {
            EndActivity(activity);
            IsSubmitting = false;
            StatusText = "";
        }
    }

    private void EndActivity(Guid activity)
    {
        _host.EndActivity(activity);
        if (_activity == activity) _activity = Guid.Empty;
    }

    private void SetCompareUrl(string url)
    {
        if (_compareUrl == url) return;
        _compareUrl = url;
        OnPropertyChanged(nameof(HasCompareUrl));
    }

    private void Progress(Guid activity, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line.Trim();
        if (Dispatcher.UIThread.CheckAccess()) Show();
        else Dispatcher.UIThread.Post(Show);

        void Show()
        {
            if (_activity != activity) return;
            StatusText = text;
            _host.ReportActivity(activity, text);
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(HasBase));
        OnPropertyChanged(nameof(HasChanges));
        OnPropertyChanged(nameof(ShowMessage));
        OnPropertyChanged(nameof(CanSubmit));
        RefreshCommand.Raise();
        SubmitCommand.Raise();
        SelectAllCommand.Raise();
        SelectNoneCommand.Raise();
    }

    private static string DescribeBase(BasisContributeScan scan)
    {
        var basis = scan.Base!;
        var source = L.Tr(basis.Source switch
        {
            BasisBaseSource.Recorded => "basisChanges.base.recorded",
            BasisBaseSource.Similarity => "basisChanges.base.similarity",
            _ => "basisChanges.base.history",
        });
        return scan.BaseCommit is { } commit
            ? L.Tr("basisChanges.basedOnCommit", Short(basis.Sha), basis.Branch, commit.Date.ToLocalTime().ToString("d"), commit.Subject, source)
            : L.Tr("basisChanges.basedOn", Short(basis.Sha), basis.Branch, source);
    }

    public static string DescribeBlock(BasisContributeScan scan) => scan.Block switch
    {
        BasisContributeBlock.GitMissing => L.Tr("basisChanges.block.gitMissing"),
        BasisContributeBlock.GitTooOld => L.Tr("basisChanges.block.gitTooOld", scan.Detail ?? "", BasisUpdateService.MinimumGitVersion.ToString(3)),
        BasisContributeBlock.NotGitRepo => L.Tr("basisChanges.block.notGitRepo"),
        BasisContributeBlock.NoCommits => L.Tr("basisChanges.block.noCommits"),
        BasisContributeBlock.UpdateInProgress => L.Tr("basisChanges.block.updateInProgress"),
        BasisContributeBlock.OperationInProgress => L.Tr("basisChanges.block.operationInProgress", scan.Detail ?? "git"),
        BasisContributeBlock.BaseUnknown => scan.Detail is { Length: > 0 } reason ? L.Tr("basisChanges.block.baseUnknownBecause", reason) : L.Tr("basisChanges.block.baseUnknown"),
        BasisContributeBlock.BaseMissing => L.Tr("basisChanges.block.baseMissing", Short(scan.Detail ?? "")),
        BasisContributeBlock.BasisFolderMissing => L.Tr("basisChanges.block.basisFolderMissing", scan.Detail ?? ""),
        _ => scan.Detail is { Length: > 0 } detail ? L.Tr("basisChanges.block.readFailedBecause", detail) : L.Tr("basisChanges.block.readFailed"),
    };

    private static string Short(string sha) => sha.Length > 9 ? sha[..9] : sha;
}
