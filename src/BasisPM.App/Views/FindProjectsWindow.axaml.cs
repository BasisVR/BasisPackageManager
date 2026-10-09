using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BasisPM.App.Localization;
using BasisPM.App.ViewModels;
using BasisPM.Core.Models;
using BasisPM.Core.Services;

namespace BasisPM.App.Views;

public partial class FindProjectsWindow : Window
{
    public FindProjectsWindow() => InitializeComponent();

    private FindProjectsViewModel? Search => DataContext as FindProjectsViewModel;

    public static async Task<string?> PickFolderAsync(Window owner, string? startIn)
    {
        var start = string.IsNullOrEmpty(startIn) || !Directory.Exists(startIn) ? null : await owner.StorageProvider.TryGetFolderFromPathAsync(startIn);
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Tr("installs.picker.findProjectsFolder"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return folders?.FirstOrDefault()?.TryGetLocalPath();
    }

    protected override void OnClosed(EventArgs e)
    {
        Search?.Stop();
        base.OnClosed(e);
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        if (Search is not { } search) return;
        try
        {
            if (await PickFolderAsync(this, search.Folder) is { Length: > 0 } folder) await search.SearchAsync(folder);
        }
        catch (Exception ex) { DiagnosticLog.Write("Choosing a folder to search for Basis projects", ex); }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        Search?.Stop();
        Close(Search?.Selected ?? Array.Empty<FoundProject>());
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
