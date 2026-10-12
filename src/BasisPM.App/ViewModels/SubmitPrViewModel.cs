using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public interface ISubmitPrHost
{
    Task<IReadOnlyList<BasisDiffLine>> GetDiffAsync(GitFileChange change, CancellationToken ct);
    Task<string?> GetTokenAsync();
    void RememberToken(string token);
    Task<GitHubUser?> GetUserAsync(string token);
}

public sealed record SubmitPrDraft(PrRequest Request, string Token, GitHubUser User);

public sealed class PrChangeItem : ObservableObject
{
    private bool _isSelected;

    public PrChangeItem(GitFileChange change) => Change = change;

    public GitFileChange Change { get; }
    public string DisplayPath => Change.Path;
    public bool IsAdded => Change.Kind is GitChangeKind.Added or GitChangeKind.Untracked;
    public bool IsDeleted => Change.Kind == GitChangeKind.Deleted;
    public bool IsModified => !IsAdded && !IsDeleted;
    public string KindLetter => Change.Kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked => "A",
        GitChangeKind.Deleted => "D",
        GitChangeKind.Renamed => "R",
        GitChangeKind.Conflicted => "U",
        _ => "M",
    };
    public string KindLabel => L.Tr(Change.Kind switch
    {
        GitChangeKind.Added or GitChangeKind.Untracked => "dialog.submitPr.kind.added",
        GitChangeKind.Deleted => "dialog.submitPr.kind.deleted",
        GitChangeKind.Renamed => "dialog.submitPr.kind.renamed",
        GitChangeKind.Conflicted => "dialog.submitPr.kind.conflicted",
        _ => "dialog.submitPr.kind.modified",
    });

    public bool IsSelected { get => _isSelected; internal set => SetField(ref _isSelected, value); }
}

public sealed class SubmitPrViewModel : ObservableObject
{
    private const string FallbackBranch = "basis-pm-edit";
    private readonly ISubmitPrHost _host;
    private CancellationTokenSource? _diffLoad;
    private PrChangeItem? _selectedFile;
    private IReadOnlyList<DiffLineItem> _diffLines = Array.Empty<DiffLineItem>();
    private string _diffTitle = "";
    private string _diffHint;
    private bool _isDiffLoading;
    private bool _isSigningIn;
    private string _errorText = "";
    private string _title;
    private string _body = "";
    private string _branch;

    public SubmitPrViewModel(ISubmitPrHost host, string packageId, IReadOnlyList<GitFileChange> changes)
    {
        _host = host;
        PackageId = string.IsNullOrWhiteSpace(packageId) ? "package" : packageId;
        var slug = new string(PackageId.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        _title = L.Tr("dialog.submitPr.defaultTitle", PackageId);
        _branch = $"basis-edit-{slug}";
        _diffHint = L.Tr("dialog.submitPr.diff.pick");
        Files = changes.Select(c => new PrChangeItem(c)).ToList();
        SelectFileCommand = new RelayCommand<PrChangeItem>(SelectFile);
    }

    public Func<Task<string?>>? AskForToken { get; set; }

    public string PackageId { get; }
    public string Subtitle => L.Tr("dialog.submitPr.contributeHint", PackageId);
    public IReadOnlyList<PrChangeItem> Files { get; }
    public string FilesText => Files.Count == 1 ? L.Tr("dialog.submitPr.filesOne") : L.Tr("dialog.submitPr.files", Files.Count);
    public RelayCommand<PrChangeItem> SelectFileCommand { get; }
    public PrChangeItem? SelectedFile => _selectedFile;

    public IReadOnlyList<DiffLineItem> DiffLines { get => _diffLines; private set { if (SetField(ref _diffLines, value)) OnPropertyChanged(nameof(HasDiff)); } }
    public bool HasDiff => _diffLines.Count > 0;
    public string DiffTitle { get => _diffTitle; private set => SetField(ref _diffTitle, value); }
    public string DiffHint { get => _diffHint; private set => SetField(ref _diffHint, value); }
    public bool IsDiffLoading { get => _isDiffLoading; private set => SetField(ref _isDiffLoading, value); }

    public string Title { get => _title; set => SetField(ref _title, value); }
    public string Body { get => _body; set => SetField(ref _body, value); }
    public string Branch { get => _branch; set => SetField(ref _branch, value); }

    public bool IsSigningIn { get => _isSigningIn; private set { if (SetField(ref _isSigningIn, value)) OnPropertyChanged(nameof(IsIdle)); } }
    public bool IsIdle => !_isSigningIn;
    public string ErrorText { get => _errorText; private set { if (SetField(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _errorText.Length > 0;

    public void SelectFile(PrChangeItem? file)
    {
        if (file is null) return;
        if (_selectedFile is not null) _selectedFile.IsSelected = false;
        _selectedFile = file;
        file.IsSelected = true;
        OnPropertyChanged(nameof(SelectedFile));
        _ = LoadDiffAsync(file);
    }

    private async Task LoadDiffAsync(PrChangeItem file)
    {
        _diffLoad?.Cancel();
        var load = _diffLoad = new CancellationTokenSource();
        DiffTitle = file.DisplayPath;
        DiffLines = Array.Empty<DiffLineItem>();
        DiffHint = L.Tr("dialog.submitPr.diff.loading");
        IsDiffLoading = true;
        try
        {
            var lines = await _host.GetDiffAsync(file.Change, load.Token);
            if (load.IsCancellationRequested) return;
            DiffLines = lines.Select(l => new DiffLineItem(l.Text.Replace("\t", "    "), l.Kind)).ToList();
            DiffHint = L.Tr("dialog.submitPr.diff.pick");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Showing the changes of {file.DisplayPath}", ex);
            if (!load.IsCancellationRequested) DiffHint = L.Tr("dialog.submitPr.diff.failed", ex.Message);
        }
        finally { if (_diffLoad == load) IsDiffLoading = false; }
    }

    public async Task<SubmitPrDraft?> SignInAsync()
    {
        if (_isSigningIn || string.IsNullOrWhiteSpace(_title)) return null;
        IsSigningIn = true;
        ErrorText = "";
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
                ErrorText = L.Tr("develop.status.signInFirst");
                return null;
            }
            var user = await _host.GetUserAsync(token);
            if (user is null)
            {
                ErrorText = L.Tr("develop.status.loginUnverified");
                return null;
            }
            var request = new PrRequest(_title.Trim(), string.IsNullOrWhiteSpace(_body) ? null : _body.Trim(), string.IsNullOrWhiteSpace(_branch) ? FallbackBranch : _branch.Trim(), null);
            return new SubmitPrDraft(request, token, user);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Signing in to open a package pull request", ex);
            ErrorText = L.Tr("develop.status.prError", ex.Message);
            return null;
        }
        finally { IsSigningIn = false; }
    }
}
