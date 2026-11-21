using BasisPM.App.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private RelayCommand? _licensesCommand;
    private RelayCommand? _projectFilesCommand;

    public RelayCommand LicensesCommand => _licensesCommand ??= new RelayCommand(ShowLicensesAsync);
    public RelayCommand ProjectFilesCommand => _projectFilesCommand ??= new RelayCommand(ShowProjectFilesAsync);

    private async Task ShowProjectFilesAsync()
    {
        if (_install is not { HasUnityProject: true } install) return;
        var model = new ProjectFilesViewModel(install.UnityProjectPath, install.DisplayName, _gitService, () => _catalog, async progress =>
        {
            if (!install.HasUnityProject) return null;
            var result = await BasisDev.RestoreAllAsync(install, switchListed: true, progress);
            return result.Ok ? null : result.Message;
        }, async () =>
        {
            if (ReferenceEquals(install, _install)) await ReloadInstalledAsync();
        });
        _ = model.LoadAsync();
        await Dialogs.ShowProjectFilesAsync(model);
    }

    private async Task ShowLicensesAsync()
    {
        if (_install is not { HasUnityProject: true } install) return;
        var model = new LicensesViewModel(install.UnityProjectPath, install.DisplayName);
        _ = model.LoadAsync();
        await Dialogs.ShowLicensesAsync(model);
    }
}
