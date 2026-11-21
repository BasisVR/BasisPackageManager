using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;

namespace BasisPM.App.Views;

public partial class ProjectFilesWindow : Window
{
    public ProjectFilesWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnOverride(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BpmPackageItem item) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = L.Tr("bpm.picker.override", item.Id), AllowMultiple = false });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        if (!File.Exists(Path.Combine(path, "package.json")))
        {
            (DataContext as ProjectFilesViewModel)?.ReportProblem(L.Tr("bpm.override.noPackage", path));
            return;
        }
        await item.SetOverrideAsync(path);
    }
}
