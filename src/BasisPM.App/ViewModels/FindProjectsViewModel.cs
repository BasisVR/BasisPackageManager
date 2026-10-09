using System.Collections.ObjectModel;
using Avalonia.Threading;
using BasisPM.App.Localization;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed class FindProjectsViewModel : ObservableObject
{
    private readonly BasisInstallService _installs;
    private readonly IReadOnlyList<BasisInstall> _existing;
    private CancellationTokenSource? _search;
    private string _folder = "";
    private bool _isSearching;
    private bool _stopped;
    private int _searched;
    private long _lastReport;

    public FindProjectsViewModel(BasisInstallService installs, IReadOnlyList<BasisInstall> existing)
    {
        _installs = installs;
        _existing = existing;
        StopCommand = new RelayCommand(Stop);
    }

    public ObservableCollection<FoundProjectRow> Results { get; } = new();
    public RelayCommand StopCommand { get; }
    public string IntroText => L.Tr("dialog.findProjects.intro", BasisInstallService.ProjectSearchDepth);
    public string Folder { get => _folder; private set => SetField(ref _folder, value); }
    public bool IsSearching { get => _isSearching; private set { if (SetField(ref _isSearching, value)) RaiseStatus(); } }
    public bool HasResults => Results.Count > 0;
    public bool ShowNothingFound => !_isSearching && Results.Count == 0 && _folder.Length > 0;
    public bool CanAdd => Results.Any(r => r.IsSelected);
    public IReadOnlyList<FoundProject> Selected => Results.Where(r => r.IsSelected).Select(r => r.Project).ToList();

    public string StatusText => _isSearching
        ? L.Tr("dialog.findProjects.searching", Volatile.Read(ref _searched).ToString("N0"))
        : L.Tr(_stopped ? "dialog.findProjects.stopped" : "dialog.findProjects.done", Results.Count, _searched.ToString("N0"));

    public async Task SearchAsync(string folder)
    {
        _search?.Cancel();
        var search = _search = new CancellationTokenSource();
        Folder = folder;
        Results.Clear();
        _searched = 0;
        _stopped = false;
        _lastReport = 0;
        IsSearching = true;
        try
        {
            await Task.Run(() =>
            {
                foreach (var project in _installs.FindProjects(folder, count => Report(count, search), search.Token))
                    Dispatcher.UIThread.Post(() => { if (ReferenceEquals(search, _search)) Add(project); });
            }, search.Token);
        }
        catch (OperationCanceledException) { _stopped = true; }
        catch (Exception ex) { DiagnosticLog.Write($"Searching {folder} for Basis projects", ex); _stopped = true; }
        finally
        {
            if (ReferenceEquals(search, _search)) IsSearching = false;
        }
    }

    public void Stop() => _search?.Cancel();

    private void Report(int count, CancellationTokenSource search)
    {
        Volatile.Write(ref _searched, count);
        var now = Environment.TickCount64;
        if (now - _lastReport < 100) return;
        _lastReport = now;
        Dispatcher.UIThread.Post(() => { if (ReferenceEquals(search, _search)) OnPropertyChanged(nameof(StatusText)); });
    }

    private void Add(FoundProject project)
    {
        var added = _existing.Any(i => Platform.PathsEqual(i.RepoRoot, project.RepoRoot) || Platform.PathsEqual(i.UnityProjectPath, project.UnityProjectPath));
        var row = new FoundProjectRow(project, added);
        row.SelectionChanged += () => OnPropertyChanged(nameof(CanAdd));
        Results.Add(row);
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(ShowNothingFound));
        OnPropertyChanged(nameof(CanAdd));
    }
}

public sealed class FoundProjectRow : ObservableObject
{
    private bool _isSelected;

    public FoundProjectRow(FoundProject project, bool alreadyAdded)
    {
        Project = project;
        AlreadyAdded = alreadyAdded;
        _isSelected = !alreadyAdded;
    }

    public event Action? SelectionChanged;
    public FoundProject Project { get; }
    public bool AlreadyAdded { get; }
    public bool CanSelect => !AlreadyAdded;
    public string Name => Project.Name;
    public string Location => Project.RepoRoot;
    public string UnityLabel => L.Tr("dialog.findProjects.unity", Project.UnityVersion);

    public bool IsSelected
    {
        get => _isSelected;
        set { if (SetField(ref _isSelected, value && CanSelect)) SelectionChanged?.Invoke(); }
    }
}
