using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using BasisPM.App.Localization;

namespace BasisPM.App.Views;

public sealed record UnityChangeBackupResult(BackupChoice Choice, string Folder);

public partial class UnityChangeBackupDialog : Window
{
    public UnityChangeBackupDialog()
    {
        InitializeComponent();
    }

    public UnityChangeBackupDialog(string project, string fromVersion, string toVersion, string folder) : this()
    {
        MessageText.Text = L.Tr("dialog.unityBackup.body", project, fromVersion, toVersion);
        FolderBox.Text = folder;
    }

    private string Folder => FolderBox.Text?.Trim() ?? "";

    private void OnFolderChanged(object? sender, TextChangedEventArgs e) => BackupButton.IsEnabled = Folder.Length > 0 && Path.IsPathFullyQualified(Folder);

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var start = Directory.Exists(Folder) ? await StorageProvider.TryGetFolderFromPathAsync(Folder) : null;
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = L.Tr("dialog.unityBackup.pickerTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
        {
            FolderBox.Text = path;
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(new UnityChangeBackupResult(BackupChoice.Cancel, Folder));
    private void OnContinue(object? sender, RoutedEventArgs e) => Close(new UnityChangeBackupResult(BackupChoice.Continue, Folder));
    private void OnBackup(object? sender, RoutedEventArgs e) => Close(new UnityChangeBackupResult(BackupChoice.Backup, Folder));
}
