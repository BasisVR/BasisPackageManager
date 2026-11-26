namespace BasisPM.App.ViewModels;

/// <summary>The Manage Project page: the project's tools, its packages, and its server.</summary>
public sealed class ProjectViewModel : ObservableObject
{
    public const int PackagesTab = 0;
    public const int ServerTab = 1;

    private int _selectedTab;

    public PackagesViewModel Packages { get; }
    public ServerViewModel Server { get; }
    public InstallsViewModel Projects { get; }
    public RelayCommand BackCommand { get; }

    public ProjectViewModel(PackagesViewModel packages, ServerViewModel server, InstallsViewModel projects, Action back)
    {
        Packages = packages;
        Server = server;
        Projects = projects;
        BackCommand = new RelayCommand(back);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetField(ref _selectedTab, value))
            {
                return;
            }
            OnPropertyChanged(nameof(IsPackagesTab));
            OnPropertyChanged(nameof(IsServerTab));
        }
    }

    public bool IsPackagesTab => _selectedTab == PackagesTab;
    public bool IsServerTab => _selectedTab == ServerTab;
}
