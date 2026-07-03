using System.Collections.ObjectModel;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class ChangesViewModel : ObservableObject
{
    private readonly GitService _git;
    private readonly MainWindowViewModel _shell;

    private BasisInstall? _install;
    private GitFileChange? _selectedChange;
    private string _diffText = "";
    private string _header = "";
    private bool _isBusy;

    public ObservableCollection<GitFileChange> Changes { get; } = new();

    public string InstallName => _install?.DisplayName ?? "No install selected";
    public bool HasInstall => _install is not null;
    public string Header { get => _header; private set => SetField(ref _header, value); }
    public bool IsBusy { get => _isBusy; set => SetField(ref _isBusy, value); }
    public bool IsClean => HasInstall && Changes.Count == 0 && !_isBusy;

    public string DiffText { get => _diffText; private set => SetField(ref _diffText, value); }

    public GitFileChange? SelectedChange
    {
        get => _selectedChange;
        set
        {
            if (SetField(ref _selectedChange, value))
                _ = LoadDiffAsync(value);
        }
    }

    public RelayCommand RefreshCommand { get; }

    public ChangesViewModel(GitService git, MainWindowViewModel shell)
    {
        _git = git;
        _shell = shell;
        RefreshCommand = new RelayCommand(RefreshAsync);
    }

    public void SetActiveInstall(BasisInstall install)
    {
        _install = install;
        OnPropertyChanged(nameof(InstallName));
        OnPropertyChanged(nameof(HasInstall));
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        Changes.Clear();
        DiffText = "";
        _selectedChange = null;
        OnPropertyChanged(nameof(SelectedChange));

        if (_install is null)
        {
            Header = "";
            OnPropertyChanged(nameof(IsClean));
            return;
        }
        if (!_install.IsGitRepo)
        {
            Header = "This install is not a git repository, so local changes can't be tracked.";
            OnPropertyChanged(nameof(IsClean));
            return;
        }

        IsBusy = true;
        OnPropertyChanged(nameof(IsClean));
        try
        {
            var status = await _git.GetStatusAsync(_install.RepoRoot);
            foreach (var c in status.Changes) Changes.Add(c);
            Header = status.IsClean
                ? $"{status.Branch} · {status.ShortCommit} — working tree clean"
                : $"{status.Branch} · {status.ShortCommit} — {status.ChangeCount} changed file{(status.ChangeCount == 1 ? "" : "s")}";
            if (Changes.Count > 0) SelectedChange = Changes[0];
        }
        catch (Exception ex)
        {
            Header = $"git error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsClean));
        }
    }

    private async Task LoadDiffAsync(GitFileChange? change)
    {
        if (_install is null || change is null)
        {
            DiffText = "";
            return;
        }
        try
        {
            DiffText = await _git.GetDiffAsync(_install.RepoRoot, change);
        }
        catch (Exception ex)
        {
            DiffText = $"Could not load diff: {ex.Message}";
        }
    }
}
