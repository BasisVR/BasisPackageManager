using System.Collections.ObjectModel;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed record ProjectPartsChoice(IReadOnlyList<BasisPart> LeaveOut, bool DeleteIgnored);

public sealed class ProjectPartsViewModel : ObservableObject
{
    private readonly Func<Task<BasisPartsReport>> _scan;
    private BasisPartsReport? _report;
    private bool _isLoading = true;
    private bool _deleteIgnored = true;
    private string _error = "";

    public ProjectPartsViewModel(string projectName, Func<Task<BasisPartsReport>> scan)
    {
        ProjectName = projectName;
        _scan = scan;
    }

    public string ProjectName { get; }
    public string Heading => L.Tr("dialog.parts.heading", ProjectName);
    public ObservableCollection<PartOption> Parts { get; } = new();
    public bool IsLoading { get => _isLoading; private set { if (SetField(ref _isLoading, value)) RaiseState(); } }
    public bool HasParts => Parts.Count > 0;
    public bool IsCustom => _report?.Mode == BasisPartsMode.Custom;
    public bool ShowNone => !_isLoading && _report is { CanChange: true } && Parts.Count == 0;
    public string Error { get => _error; private set { if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => _error.Length > 0;
    public bool CanApply => !_isLoading && _report is { CanChange: true } && Parts.Any(p => p.IsIncluded != p.WasIncluded);
    public int IgnoredToDelete => Parts.Where(p => p.IsLeavingOut).Sum(p => p.IgnoredFiles);
    public bool ShowDeleteIgnored => IgnoredToDelete > 0;
    public string DeleteIgnoredText => L.Tr("dialog.parts.deleteIgnored", IgnoredToDelete.ToString("N0"));
    public bool DeleteIgnored { get => _deleteIgnored; set => SetField(ref _deleteIgnored, value); }
    public ProjectPartsChoice Choice => new(Parts.Where(p => !p.IsIncluded).Select(p => p.Part).ToList(), _deleteIgnored);

    public async Task LoadAsync()
    {
        IsLoading = true;
        Error = "";
        try
        {
            var report = await _scan();
            _report = report;
            Parts.Clear();
            foreach (var state in report.Parts)
            {
                var option = new PartOption(state.Part, state.Included, state, !report.CanChange);
                option.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PartOption.IsIncluded)) RaiseSelection(); };
                Parts.Add(option);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Checking the parts of {ProjectName}", ex);
            Error = L.Tr("dialog.parts.error", ex.Message);
        }
        finally { IsLoading = false; }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(ShowNone));
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IgnoredToDelete));
        OnPropertyChanged(nameof(ShowDeleteIgnored));
        OnPropertyChanged(nameof(DeleteIgnoredText));
    }
}

public sealed class CloneBasisViewModel
{
    public CloneBasisViewModel(string folder, IEnumerable<string>? leaveOut)
    {
        Folder = folder;
        var skipped = BasisPartsService.FromIds(leaveOut);
        Parts = BasisPartsService.All.Select(p => new PartOption(p, !skipped.Contains(p))).ToList();
    }

    public string Folder { get; }
    public IReadOnlyList<PartOption> Parts { get; }
    public IReadOnlyList<BasisPart> LeaveOut => Parts.Where(p => !p.IsIncluded).Select(p => p.Part).ToList();
}

public sealed class PartOption : ObservableObject
{
    private readonly bool _locked;
    private bool _isIncluded;

    public PartOption(BasisPart part, bool included, BasisPartState? state = null, bool locked = false)
    {
        Part = part;
        State = state;
        WasIncluded = _isIncluded = included;
        _locked = locked;
    }

    public BasisPart Part { get; }
    public BasisPartState? State { get; }
    public bool WasIncluded { get; }
    public string Name => PartsText.Name(Part);
    public string Description => PartsText.Description(Part);
    public string Location => State?.Path ?? BasisPartsService.PathOf(Part, BasisUpdateService.BasisUnityFolder);
    public bool ShowsLocation => Location != Name;
    public bool ShowsStatus => State is not null;
    public string StatusText => L.Tr((WasIncluded, _isIncluded) switch
    {
        (true, true) => "parts.status.included",
        (true, false) => "parts.status.leavingOut",
        (false, true) => "parts.status.addingBack",
        _ => "parts.status.leftOut",
    });
    public bool HasUnsavedWork => State?.HasUnsavedWork == true;
    public string UnsavedText => State is { Unsaved.Count: > 0 } s ? L.Tr("parts.unsaved", PartsText.Sample(s.Unsaved)) : "";
    public bool CanToggle => !_locked && !(WasIncluded && HasUnsavedWork);
    public int IgnoredFiles => State?.IgnoredFiles ?? 0;
    public bool IsLeavingOut => WasIncluded && !_isIncluded;
    public bool ShowsRepositories => IsLeavingOut && State is { Repositories.Count: > 0 };
    public string RepositoriesText => State is { Repositories.Count: > 0 } s ? L.Tr("parts.repositories", PartsText.Sample(s.Repositories)) : "";

    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (!SetField(ref _isIncluded, value)) return;
            OnPropertyChanged(nameof(IsLeavingOut));
            OnPropertyChanged(nameof(ShowsRepositories));
            OnPropertyChanged(nameof(StatusText));
        }
    }
}

public static class PartsText
{
    public static string Name(BasisPart part) => L.Tr($"parts.{part.Id}.name");
    public static string Description(BasisPart part) => L.Tr($"parts.{part.Id}.description");
    public static string Names(IEnumerable<BasisPart> parts) => string.Join(", ", parts.Select(Name));

    public static string Describe(string project, BasisPartsResult result)
    {
        if (!result.Ok) return result.Failure switch
        {
            BasisPartsFailure.NotGitRepo => L.Tr("parts.failure.notGit", project),
            BasisPartsFailure.CustomSparseCheckout => L.Tr("parts.failure.custom", project),
            BasisPartsFailure.OperationInProgress => L.Tr("parts.failure.busy", project),
            BasisPartsFailure.UnsavedWork => L.Tr("parts.failure.unsaved", Sample(result.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries))),
            _ => L.Tr("parts.failure.git", result.Detail.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? ""),
        };
        if (!result.Changed) return L.Tr("parts.result.unchanged", project);
        var lines = new List<string>();
        if (result.Removed.Count > 0) lines.Add(L.Tr("parts.result.removed", Names(result.Removed), project));
        if (result.Added.Count > 0) lines.Add(L.Tr("parts.result.added", Names(result.Added), project));
        if (result.DeletedFiles > 0) lines.Add(L.Tr("parts.result.deleted", result.DeletedFiles.ToString("N0")));
        if (result.IgnoredFilesLeft > 0) lines.Add(L.Tr("parts.result.ignoredLeft", result.IgnoredFilesLeft.ToString("N0")));
        if (result.Kept.Count > 0) lines.Add(L.Tr("parts.result.kept", Sample(result.Kept)));
        return string.Join(" ", lines);
    }

    public static string Sample(IReadOnlyCollection<string> paths) =>
        paths.Count <= 3 ? string.Join(", ", paths) : L.Tr("parts.more", string.Join(", ", paths.Take(3)), paths.Count - 3);
}
