using BasisPM.App.Services;
using BasisPM.Core.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class InstallsViewModel
{
    private RelayCommand<InstallRow>? _storageCommand;
    private ProjectStorageService? _storage;

    public RelayCommand<InstallRow> StorageCommand => _storageCommand ??= new RelayCommand<InstallRow>(OpenStorageAsync);

    private ProjectStorageService Storage => _storage ??= CreateStorage();

    private ProjectStorageService CreateStorage()
    {
        var projects = new UnityProjectService();
        var registry = new MountRegistry();
        var mounts = new MountService(_git, projects, registry);
        return new ProjectStorageService(new BasisDevService(_git, projects, registry, mounts), mounts);
    }

    private async Task OpenStorageAsync(InstallRow? row)
    {
        if (row is null || !row.HasUnityProject) return;
        var model = new ProjectStorageViewModel(row.Install, row.Name, Storage,
            (label, work) => Queued(row, label, work),
            Dialogs.ConfirmAsync,
            (message, ok) => _shell.SetStatus(message, ok ? StatusKind.Success : StatusKind.Error, row.Name));
        await Dialogs.ShowStorageAsync(model);
    }
}
