using BasisPM.App.Services;

namespace BasisPM.App.ViewModels;

public sealed partial class PackagesViewModel
{
    private RelayCommand? _licensesCommand;

    public RelayCommand LicensesCommand => _licensesCommand ??= new RelayCommand(ShowLicensesAsync);

    private async Task ShowLicensesAsync()
    {
        if (_install is not { HasUnityProject: true } install) return;
        var model = new LicensesViewModel(install.UnityProjectPath, install.DisplayName);
        _ = model.LoadAsync();
        await Dialogs.ShowLicensesAsync(model);
    }
}
